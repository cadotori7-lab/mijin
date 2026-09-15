import sys

# 가장 먼저 실행돼야 한다: vision.py를 임포트하는 순간 RapidOCR 초기화 로그가
# 한글로 찍히는데, 인코딩 설정 전이면 콘솔에서 깨진다.
sys.stdout.reconfigure(encoding='utf-8')

import json
import time
import hashlib
from typing import Optional

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse
import httpx
import uvicorn

from viewer import router as viewer_router
from mijin import router as mijin_router   # 추가
from config import (
    CLOUD_BASE_URL, CLOUD_API_KEY, TARGET_MODEL,
    GEMMA_TEMPERATURE, GEMMA_TOP_P, CLOUD_TIMEOUT_SEC,
    OCR_BUDGET_DEEP, OCR_BUDGET_NORMAL,
    OCR_CROP_ENABLED, CROP_PROFILES,
    ENABLE_OBSERVATION_LOG, OBSERVATION_LOG, OBSERVE_BLOCKLIST,
    OBSERVE_CONTENT_BLOCKLIST,
    SKIP_VISION_MIN_OCR,
)
from vision import extract_and_analyze_image, _HAS_OCR, _HAS_PYGETWINDOW, _HAS_IMAGEHASH
from context import clean_messages, _load_profile

app = FastAPI()
app.include_router(viewer_router)
app.include_router(mijin_router)           # 추가


def _empty_completion_response(stream: bool):
    """화면이 안 바뀌었을 때 클라우드를 부르지 않고 돌려주는 빈 응답."""
    created = int(time.time())
    if not stream:
        return JSONResponse(content={
            "id": "chatcmpl-skip",
            "object": "chat.completion",
            "created": created,
            "model": TARGET_MODEL,
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": ""},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
        })

    async def gen():
        chunk = {
            "id": "chatcmpl-skip",
            "object": "chat.completion.chunk",
            "created": created,
            "model": TARGET_MODEL,
            "choices": [{
                "index": 0,
                "delta": {"role": "assistant", "content": ""},
                "finish_reason": "stop",
            }],
        }
        yield f"data: {json.dumps(chunk)}\n\n".encode()
        yield b"data: [DONE]\n\n"

    return StreamingResponse(gen(), media_type="text/event-stream")


def _system_message_hash(cleaned_messages) -> str:
    """system 메시지 내용의 해시. 캐시가 안 걸릴 때 '요청마다 뭐가 바뀌는지'를
    눈으로 비교하기 위한 진단용 - 값 자체엔 의미가 없다."""
    sys_text = next((m.get("content") or "" for m in cleaned_messages if m.get("role") == "system"), "")
    return hashlib.sha256(sys_text.encode("utf-8")).hexdigest()[:12]


def _log_cache_usage(usage: dict, sys_hash: str) -> None:
    """OpenAI 호환 usage.prompt_tokens_details.cached_tokens로 프롬프트 캐시가
    실제로 걸리는지 찍어본다. 이 프록시가 앞부분(프로필/기억 순서)을 아무리
    캐시 프렌들리하게 짜도, 모드가 보내는 system 메시지 자체가 매 요청 달라지면
    캐시는 걸릴 수가 없다 - system_hash가 요청마다 바뀌면 그게 원인이다."""
    if not usage:
        return
    cached = (usage.get("prompt_tokens_details") or {}).get("cached_tokens")
    total = usage.get("prompt_tokens")
    print(f"[CACHE] system_hash={sys_hash} cached_tokens={cached} prompt_tokens={total}")


def _parse_stream_usage(raw: bytes) -> Optional[dict]:
    """스트리밍 응답 바이트에서 usage가 담긴 마지막 SSE 청크를 찾는다.
    forward_payload에 stream_options.include_usage를 안 붙이면 애초에 없다."""
    usage = None
    for line in raw.split(b"\n"):
        line = line.strip()
        if not line.startswith(b"data: ") or line == b"data: [DONE]":
            continue
        try:
            chunk = json.loads(line[6:])
        except json.JSONDecodeError:
            continue
        if chunk.get("usage"):
            usage = chunk["usage"]
    return usage


@app.get("/v1/models")
async def list_models():
    """꼬미 프로그램의 연결 테스트 통과용 더미 응답"""
    return {
        "object": "list",
        "data": [{"id": TARGET_MODEL, "object": "model", "owned_by": "custom-proxy"}],
    }


# 꼬미 프로그램이 /v1 없이 바로 요청하는 경우(404 에러 방지)를 위해 두 가지 경로 모두 열어둡니다.
@app.post("/v1/chat/completions")
@app.post("/chat/completions")
async def chat_completions(request: Request):
    payload = await request.json()
    messages = payload.get("messages", [])
    stream = payload.get("stream", False)

    # 1. 화면 캡처 분석 (로컬 Qwen)
    status, screen_record = await extract_and_analyze_image(messages)

    # 2. 화면 구경 요청인데 화면이 그대로면 클라우드를 아예 부르지 않는다.
    if status == "skipped":
        return _empty_completion_response(stream)

    # 3. 메시지 정제 및 기억/분석 결과 주입
    cleaned_messages = clean_messages(messages, screen_record)
    sys_hash = _system_message_hash(cleaned_messages)

    # 4. 클라우드 Gemma 전송용 페이로드 구성
    forward_payload = {
        "model": TARGET_MODEL,
        "messages": cleaned_messages,
        "stream": stream,
        "temperature": payload.get("temperature", GEMMA_TEMPERATURE),
        "top_p": payload.get("top_p", GEMMA_TOP_P),
    }
    if stream:
        # 스트리밍은 기본적으로 usage를 안 주므로, 캐시가 실제로 걸리는지 보려면
        # 이 옵션으로 마지막에 usage만 담긴 청크를 하나 더 받아야 한다.
        forward_payload["stream_options"] = {"include_usage": True}

    headers = {
        "Authorization": f"Bearer {CLOUD_API_KEY}",
        "Content-Type": "application/json",
    }

    # 스트리밍 응답 지원
    if stream:
        async def stream_generator():
            # 기존 코드는 여기서 만든 클라이언트를 닫지 않아 연결이 계속 쌓였다.
            client = httpx.AsyncClient(timeout=CLOUD_TIMEOUT_SEC)
            raw = bytearray()
            try:
                async with client.stream(
                    "POST", f"{CLOUD_BASE_URL}/chat/completions",
                    json=forward_payload, headers=headers
                ) as response:
                    if response.status_code == 401:
                        print("\n[ERROR] 클라우드 API 키 인증 실패 (401). .env의 OLLAMA_CLOUD_API_KEY를 확인하세요.")
                    async for chunk in response.aiter_bytes():
                        raw.extend(chunk)  # 클라이언트로 보내는 바이트는 그대로, 로깅용으로만 따로 모은다
                        yield chunk
            finally:
                await client.aclose()
                _log_cache_usage(_parse_stream_usage(bytes(raw)), sys_hash)

        return StreamingResponse(stream_generator(), media_type="text/event-stream")

    # 일반 응답
    async with httpx.AsyncClient(timeout=CLOUD_TIMEOUT_SEC) as client:
        try:
            resp = await client.post(
                f"{CLOUD_BASE_URL}/chat/completions", json=forward_payload, headers=headers
            )
        except httpx.TimeoutException:
            # 500을 돌려주면 모드가 고장으로 보고 재시도해 상황을 악화시킨다.
            # 화면이 안 바뀌었을 때와 같은 빈 응답으로 조용히 넘긴다.
            print("[WARN] 클라우드 응답 시간 초과 -> 이번 턴은 건너뜀")
            return _empty_completion_response(stream)
        except httpx.HTTPError as e:
            print(f"[ERROR] 클라우드 요청 실패: {e}")
            return _empty_completion_response(stream)
        if resp.status_code == 401:
            print("\n[ERROR] 클라우드 API 키 인증 실패 (401). .env의 OLLAMA_CLOUD_API_KEY를 확인하세요.")
        resp_json = resp.json()
        _log_cache_usage(resp_json.get("usage"), sys_hash)
        return JSONResponse(status_code=resp.status_code, content=resp_json)


if __name__ == "__main__":
    print("[START] 꼬미 전용 듀얼 모델 프록시 서버 시작 (Port: 8000)")
    print(f"[OPTION] OCR 그라운딩: {'ON' if _HAS_OCR else 'OFF (pip install rapidocr onnxruntime)'}")
    print(f"[OPTION] 활성 창 감지: {'ON' if _HAS_PYGETWINDOW else 'OFF (pip install pygetwindow)'}")
    print(f"[OPTION] 화면 변화 감지 스킵: {'ON' if _HAS_IMAGEHASH else 'OFF (pip install imagehash pillow)'}")
    print(f"[OPTION] OCR 예산: 작업창 {OCR_BUDGET_DEEP}자 / 그 외 {OCR_BUDGET_NORMAL}자")
    if OCR_CROP_ENABLED:
        print(f"[OPTION] OCR 크롭: ON (프로파일 {len(CROP_PROFILES)}개 + 기본값)")
        for k, v in CROP_PROFILES.items():
            print(f"           - {k}: {v}")
    else:
        print("[OPTION] OCR 크롭: OFF")
    print(f"[OPTION] 관찰 로그: {'ON -> ' + OBSERVATION_LOG if ENABLE_OBSERVATION_LOG else 'OFF'}")
    if ENABLE_OBSERVATION_LOG:
        print(f"[OPTION] 관찰 제외 창: {len(OBSERVE_BLOCKLIST)}개 ({', '.join(OBSERVE_BLOCKLIST[:4])} ...)")
    print(f"[OPTION] 비전 생략 임계값: OCR {SKIP_VISION_MIN_OCR}자 이상 + 텍스트 전용 창")
    _profile_len = len(_load_profile())
    print(f"[OPTION] 프로필 주입: {'ON -> ' + str(_profile_len) + '자' if _profile_len else 'OFF (profile.txt 없음)'}")

    import memory
    memory.load()

    uvicorn.run(app, host="127.0.0.1", port=8000)
