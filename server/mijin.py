"""미진이 본체(Unity) 전용 엔드포인트.

기존 /v1/chat/completions는 모드가 시스템 프롬프트와 대화 기록을 통째로
보내준다는 전제로 짜여 있다. 미진이 본체에는 그걸 해줄 모드가 없으므로
여기서 페르소나와 대화 기록을 직접 들고 있는다.

Unity는 이 한 곳만 호출하면 된다:
    POST /mijin/chat
    {"kind": "chat" | "monologue" | "screen" | "event",
     "text": "사용자 입력 (없으면 생략)",
     "image": "base64 스크린샷 (없으면 생략)"}
  ->  {"status": "ok" | "skipped", "text": "대사"}

kind="event"는 찌르기·블라인드처럼 사람이 친 말이 아니라 클라이언트가 만든
지문이다. chat과 섞어 쌓으면 대화 기록 예산을 나눠 먹으므로 memory.py에서
따로 쌓는다 (memory.py 모듈 docstring 참고).
"""

import asyncio
import os
import re
from typing import Optional, List, Dict, Any

import httpx
from fastapi import APIRouter
from pydantic import BaseModel

import distill
import episode
import memory
from config import (
    CLOUD_BASE_URL, CLOUD_API_KEY, TARGET_MODEL,
    GEMMA_TEMPERATURE, GEMMA_TOP_P, CLOUD_TIMEOUT_SEC,
    PERSONA_FILE, DUMP_PROMPT, LAST_PROMPT_FILE,
)
from vision import extract_and_analyze_image
from context import clean_messages

router = APIRouter()

# 혼잣말 요청에는 사용자 발화가 없으므로 대신 넣어줄 지시문
MONOLOGUE_PROMPT = "(파트너님은 말이 없다. 지금 보고 있는 화면에 대해 미진이의 생각을 말한다.)"

# 말풍선과 TTS에 들어가면 곤란한 문자. 페르소나로 1차로 막고 여기서 한 번 더 거른다.
# 대괄호([ ])는 일부러 안 지운다 - 대사 맨 앞의 감정 태그([신나서] 등)가 여기 담겨서
# Unity(MijinTalk.cs)로 그대로 넘어가야 한다. 말풍선용/TTS용 분리는 Unity 쪽에서 한다.
SPEECH_STRIP_PATTERN = re.compile(r"[*_`#>]|ㅋ{2,}|ㅎ{2,}|[~]{1,}")

_persona_cache = {"key": None, "text": ""}


class MijinRequest(BaseModel):
    kind: str = "chat"          # chat | monologue | screen | event
    text: Optional[str] = None
    image: Optional[str] = None  # base64 (data URI 여부는 vision.py가 알아서 처리)
    window: Optional[str] = None      # 추가

def _load_persona() -> str:
    """페르소나 파일을 읽는다. 파일이 바뀔 때만 다시 읽는다."""
    try:
        st = os.stat(PERSONA_FILE)
        key = (st.st_mtime_ns, st.st_size)
    except OSError:
        return _persona_cache["text"]

    if key == _persona_cache["key"]:
        return _persona_cache["text"]

    try:
        with open(PERSONA_FILE, encoding="utf-8") as f:
            text = f.read().strip()
    except OSError as e:
        print(f"[WARN] 페르소나 읽기 실패: {e}")
        return _persona_cache["text"]

    _persona_cache["key"] = key
    _persona_cache["text"] = text
    print(f"[INFO] 페르소나 갱신: {len(text)}자")
    return text


def _clean_for_speech(text: str) -> str:
    """말풍선과 TTS에 들어가면 곤란한 문자를 걷어낸다.

    페르소나 프롬프트로 1차로 막고 있지만 새어 나오는 것이 있어서 한 번 더 거른다.
    """
    text = SPEECH_STRIP_PATTERN.sub("", text)
    text = re.sub(r"\s+", " ", text).strip()
    return text.strip('"\u201c\u201d\'')


async def _call_cloud(messages: List[Dict[str, Any]]) -> Optional[str]:
    payload = {
        "model": TARGET_MODEL,
        "messages": messages,
        "stream": False,
        "temperature": GEMMA_TEMPERATURE,
        "top_p": GEMMA_TOP_P,
    }
    headers = {
        "Authorization": f"Bearer {CLOUD_API_KEY}",
        "Content-Type": "application/json",
    }
    async with httpx.AsyncClient(timeout=CLOUD_TIMEOUT_SEC) as client:
        try:
            resp = await client.post(
                f"{CLOUD_BASE_URL}/chat/completions", json=payload, headers=headers
            )
        except httpx.TimeoutException:
            print("[WARN] 클라우드 응답 시간 초과 -> 이번 턴은 건너뜀")
            return None
        except httpx.HTTPError as e:
            print(f"[ERROR] 클라우드 요청 실패: {e}")
            return None

    if resp.status_code == 401:
        print("\n[ERROR] 클라우드 API 키 인증 실패 (401). .env를 확인하세요.")
        return None
    if resp.status_code != 200:
        print(f"[ERROR] 클라우드 응답 오류 {resp.status_code}: {resp.text[:200]}")
        return None

    try:
        return resp.json()["choices"][0]["message"]["content"]
    except (KeyError, IndexError, ValueError) as e:
        print(f"[ERROR] 클라우드 응답 파싱 실패: {e}")
        return None


def _dump_prompt(cleaned: List[Dict[str, Any]]) -> None:
    """클라우드로 나가기 직전의 messages를 last_prompt.txt에 덮어쓴다 (DUMP_PROMPT=True일 때만).

    누적하지 않고 매번 덮어쓴다 - 쌓으면 디스크가 차고, 지난 화면 내용이 계속 남는다."""
    lines = []
    for msg in cleaned:
        lines.append(f"───── {msg.get('role', '?')} ─────")
        lines.append(str(msg.get("content", "")))
        lines.append("")
    try:
        LAST_PROMPT_FILE.write_text("\n".join(lines), encoding="utf-8")
    except OSError as e:
        print(f"[WARN] 프롬프트 덤프 실패: {e}")


@router.post("/mijin/chat")
async def mijin_chat(req: MijinRequest):
    asyncio.create_task(episode.ensure_recent())
    asyncio.create_task(distill.ensure())

    # ── 1. 화면 분석 ──────────────────────────────
    # vision.py는 OpenAI 형식 메시지를 받으므로 이미지 한 장짜리 목록으로 감싼다.
    screen_record = None
    if req.image:
        fake_messages = [{
            "role": "user",
            "content": [{"type": "image_url", "image_url": {"url": req.image}}],
        }]
        status, screen_record = await extract_and_analyze_image(
            fake_messages, window_title=req.window or None
        )

        # 화면이 그대로면 혼잣말은 통째로 건너뛴다 (요금이 여기서 가장 많이 샌다).
        # 반면 사용자가 말을 걸었거나(chat) 이벤트가 일어났다면(event) 화면과
        # 무관하게 대답해야 한다. 이벤트는 지금 이미지를 안 보내 이 분기를 실제로
        # 타진 않지만, 나중에 이미지를 붙여도 찌르기가 무시되지 않도록 맞춰둔다.
        if status == "skipped" and req.kind not in ("chat", "event"):
            return {"status": "skipped", "text": ""}

    # ── 2. 이번 턴의 사용자 발화 ──────────────────
    user_text = (req.text or "").strip()
    if not user_text:
        user_text = MONOLOGUE_PROMPT

    # ── 3. 메시지 조립 ────────────────────────────
    # clean_messages가 프로필을 system에, 화면 기억과 리마인더를 마지막 user에 넣는다.
    # episode_block()은 하루 한 번만 바뀌므로 system 메시지 뒤에 붙여도 캐시가 거의 유지된다.
    persona = _load_persona()
    system_text = persona + memory.episode_block() if persona else memory.episode_block()

    messages = [{"role": "system", "content": system_text}] if system_text else []
    messages += memory.build_history()
    messages.append({"role": "user", "content": user_text})

    cleaned = clean_messages(messages, screen_record)

    if DUMP_PROMPT:
        _dump_prompt(cleaned)

    # ── 4. 클라우드 호출 ──────────────────────────
    raw = await _call_cloud(cleaned)
    if raw is None:
        return {"status": "skipped", "text": ""}

    line = _clean_for_speech(raw)
    if not line:
        return {"status": "skipped", "text": ""}

    # ── 5. 기록 갱신 ──────────────────────────────
    # 주입된 내용이 아니라 원래 발화만 남긴다. 화면 기억이 대화 기록에까지
    # 중복으로 쌓이면 같은 화면 설명이 매 요청 몇 번씩 실려 나간다.
    # 대화/혼잣말을 파일로 따로 쌓아서, 잦은 혼잣말이 진짜 대화를 밀어내지 않고
    # 프록시를 재시작해도 기억이 이어지게 한다.
    if req.kind == "chat":
        memory.add_chat(user_text, line)
    elif req.kind == "event":
        memory.add_event(user_text, line)
    else:
        memory.add_monologue(line)

    print(f"[MIJIN] {req.kind} -> {line[:60]}")
    return {"status": "ok", "text": line}


@router.post("/mijin/reset")
async def mijin_reset():
    """프롬프트에 실리는 기록을 비운다 (지우지 않고 아카이브로 옮김)."""
    return {"status": "ok", **memory.reset()}


@router.post("/mijin/episode")
async def mijin_episode(date: str = "", force: bool = False):
    """회고를 손수 만든다. date를 비우면 어제. 확인용."""
    from datetime import date as _date, timedelta
    day = date or (_date.today() - timedelta(days=1)).isoformat()
    result = await episode.build(day, force=force)
    return {"status": "ok" if result else "skipped", "date": day, **(result or {})}


@router.post("/mijin/distill")
async def mijin_distill(force: bool = False):
    """프로필을 손수 증류한다. 확인용."""
    result = await distill.build(force=force)
    return {"status": "ok" if result else "skipped", "profile": result or ""}
