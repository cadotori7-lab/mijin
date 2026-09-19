"""화면 캡처 분석 (시각처리).

메시지에서 스크린샷을 찾아 OCR + 로컬 비전 모델로 분석하고, 클라우드로 보낼
(상태, 기록) 튜플을 만든다. 설정값은 전부 config.py에 있다.

proxy_server.py에서 쓰는 진입점은 extract_and_analyze_image() 하나다.
"""

import io
import re
import json
import time
import base64
import asyncio
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

import httpx

from config import (
    LOCAL_OLLAMA_URL, VISION_MODEL,
    VISION_NUM_CTX, VISION_NUM_PREDICT_SCENE, VISION_NUM_PREDICT_FULL,
    VISION_NUM_PREDICT_RETRY, VISION_TIMEOUT_SEC,
    PHASH_CHANGE_THRESHOLD, VISION_MAX_WIDTH,
    OCR_MIN_CONFIDENCE, OCR_MIN_LINE_LEN,
    OCR_BUDGET_DEEP, OCR_BUDGET_NORMAL, OCR_BRIEF_LEN, SCENE_BRIEF_LEN,
    DEEP_WINDOWS, DEEP_SIGNALS, TEXT_ONLY_WINDOWS, SKIP_VISION_MIN_OCR,
    OCR_BLOCKLIST, BLOCKED_SCREEN_MODE, BLOCKED_TITLE_LABEL,
    SECRET_PATTERNS,
    OCR_CROP_ENABLED, CROP_PROFILES, CROP_DEFAULT,
    OBSERVATION_LOG, ENABLE_OBSERVATION_LOG,
    OBSERVE_BLOCKLIST, OBSERVE_CONTENT_BLOCKLIST,
    VISION_PROMPT, SCENE_ONLY_PROMPT, VISION_RETRY_PROMPT,
)

# Pillow는 비전 모델에 보내기 전 이미지 다운스케일에 필수로 쓰인다 (VISION_MAX_WIDTH 참고).
from PIL import Image

# ============= 선택적 의존성 (없어도 서버는 동작) =============
try:
    import imagehash
    _HAS_IMAGEHASH = True
except ImportError:
    _HAS_IMAGEHASH = False

try:
    from rapidocr import RapidOCR, LangRec, EngineType, ModelType, OCRVersion
    # 기본(ch/en) 인식 모델은 한글 자모를 아예 못 읽는다 (Hangul이 사전에 없음 - 실측 확인됨).
    # korean 언어팩을 쓰면 한글은 물론 영문/숫자/코드 텍스트도 충분히 잘 인식해서
    # (실측: "proxy_server.py import json" 등 정확히 인식) 별도로 두 엔진을 돌릴 필요는 없었음.
    #
    # ★ use_cls(방향 분류기)를 끄는 이유:
    #   켜두면 줄 방향을 잘못 판정해 글자를 180도 뒤집어 읽는 경우가 있다.
    #   (실측: "제거" -> "L2h", "# 상단 15% 제거" -> "meJeaje juaquo doJ")
    #   모니터 스크린샷은 항상 정방향이라 판별 자체가 불필요하다.
    # 파라미터 키 이름은 RapidOCR 버전마다 달라서 순서대로 시도한다.
    _base_params = {
        "Rec.lang_type": LangRec.KOREAN,
        "Rec.engine_type": EngineType.ONNXRUNTIME,
        "Rec.model_type": ModelType.MOBILE,
        "Rec.ocr_version": OCRVersion.PPOCRV4,
    }
    _ocr_engine = None
    for _cls_key in ("Global.use_cls", "Cls.use_cls", "use_cls", None):
        try:
            _p = dict(_base_params)
            if _cls_key:
                _p[_cls_key] = False
            _ocr_engine = RapidOCR(params=_p)
            print(f"[INFO] OCR 방향 분류기: {'OFF (' + _cls_key + ')' if _cls_key else 'ON (끄기 실패 - 기본값 사용)'}")
            break
        except Exception:
            continue
    if _ocr_engine is None:
        raise ImportError("RapidOCR 초기화 실패")
    _HAS_OCR = True
except ImportError:
    _HAS_OCR = False

try:
    import pygetwindow as gw
    _HAS_PYGETWINDOW = True
except ImportError:
    _HAS_PYGETWINDOW = False
# =============================================================

_last_phash = None
_last_observation = None

_DATA_URI_RE = re.compile(r"^data:image/\w+;base64,")


def _strip_data_uri(s: str) -> str:
    return _DATA_URI_RE.sub("", s)


def _find_last_image(messages: List[Dict[str, Any]]) -> Optional[str]:
    """가장 최근 '사용자' 메시지에서만 이미지를 찾는다.

    모드가 과거 캡처를 히스토리에 남겨두는 구조라면, 전체를 훑는 방식은
    이미지 없는 '말 걸기' 요청에서도 옛날 스크린샷을 다시 집어오게 된다
    (그러면 pHash가 같아 skipped로 클라우드 호출까지 막힘). 그래서 가장
    최근 user 메시지 하나만 보고, 거기 이미지가 없으면 더 뒤지지 않는다."""
    for msg in reversed(messages):
        if msg.get("role") != "user":
            continue
        content = msg.get("content")
        if isinstance(content, list):
            for part in reversed(content):
                if isinstance(part, dict) and part.get("type") == "image_url":
                    return part.get("image_url", {}).get("url")
        elif "images" in msg and msg.get("images"):
            return msg["images"][0]
        return None
    return None


def _compute_phash(raw_b64: str):
    """이미지의 pHash를 계산만 한다 (상태를 바꾸지 않음). 실패/미설치 시 None."""
    if not _HAS_IMAGEHASH:
        return None
    try:
        img = Image.open(io.BytesIO(base64.b64decode(raw_b64)))
        return imagehash.phash(img)
    except Exception:
        return None


def _screen_changed(new_hash) -> bool:
    """직전에 '커밋'된 해시와 비교만 한다. 커밋은 _commit_phash가 따로 한다.

    분석을 실제로 마친 프레임만 커밋해야, 도중에 실패한 프레임과 같은
    화면이 다시 들어왔을 때 재시도할 기회가 남는다."""
    if new_hash is None:
        return True
    if _last_phash is not None and (new_hash - _last_phash) < PHASH_CHANGE_THRESHOLD:
        return False
    return True


def _commit_phash(new_hash) -> None:
    global _last_phash
    if new_hash is not None:
        _last_phash = new_hash


def _downscale_for_vision(raw_b64: str) -> str:
    """비전 모델에 보낼 이미지만 축소. 해상도가 높을수록 thinking이 폭주하는 경향이 있어
    (원본 그대로 보내면 num_predict를 다 써도 본문 없이 잘리는 경우가 잦음) 원본 대신 이걸 사용한다.
    OCR은 별도로 원본 해상도를 그대로 쓰므로 텍스트 인식 정확도는 유지된다."""
    try:
        img = Image.open(io.BytesIO(base64.b64decode(raw_b64)))
        if img.width > VISION_MAX_WIDTH:
            scale = VISION_MAX_WIDTH / img.width
            img = img.resize((VISION_MAX_WIDTH, int(img.height * scale)))
        if img.mode != "RGB":
            img = img.convert("RGB")
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=85)
        return base64.b64encode(buf.getvalue()).decode()
    except Exception as e:
        print(f"[WARN] 이미지 다운스케일 실패, 원본으로 진행: {e}")
        return raw_b64


def _crop_box(window_title: str):
    """활성 창에 맞는 크롭 비율을 고른다. dict 순서대로 검사하므로
    'Google Docs'가 'Chrome'보다 앞에 있어야 한다 (독스 창 제목에 둘 다 들어있음)."""
    t = window_title.lower()
    for key, box in CROP_PROFILES.items():
        if key.lower() in t:
            return box, key
    return CROP_DEFAULT, "default"


def _crop_content_area(raw_b64: str, window_title: str = "") -> bytes:
    """탭바/주소창/사이드바/작업표시줄을 잘라내고 본문 영역만 남긴다.
    OCR은 크롭한 원본 해상도로 돌리므로 작은 글자 인식 정확도는 유지된다."""
    image_bytes = base64.b64decode(raw_b64)
    if not OCR_CROP_ENABLED:
        return image_bytes
    try:
        (l, t, r, b), profile = _crop_box(window_title)
        img = Image.open(io.BytesIO(image_bytes))
        w, h = img.size
        img = img.crop((int(w * l), int(h * t), int(w * r), int(h * b)))
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        return buf.getvalue()
    except Exception as e:
        print(f"[WARN] OCR 크롭 실패, 원본으로 진행: {e}")
        return image_bytes


def _redact_secrets(text: str) -> Tuple[str, int]:
    """OCR 결과에서 키/토큰처럼 보이는 부분을 지운다. (지운 텍스트, 지운 건수)"""
    n = 0
    for pat in SECRET_PATTERNS:
        text, k = pat.subn("[비밀정보 제거됨]", text)
        n += k
    return text, n


def _run_ocr(raw_b64: str, window_title: str = "") -> str:
    if not _HAS_OCR:
        return ""
    try:
        image_bytes = _crop_content_area(raw_b64, window_title)
        result = _ocr_engine(image_bytes)
        if not result.txts:
            return ""
        # 신뢰도 낮은 줄(작은 툴바 아이콘 등 오인식)은 걸러낸다.
        # 추가로 너무 짧은 줄("*", "-", "허" 등)은 아이콘 오인식이라 버린다.
        lines = [
            text for text, conf in zip(result.txts, result.scores)
            if conf >= OCR_MIN_CONFIDENCE and len(text.strip()) >= OCR_MIN_LINE_LEN
        ]
        text = "\n".join(lines)
        text, redacted = _redact_secrets(text)
        if redacted:
            print(f"[INFO] OCR에서 비밀정보 의심 {redacted}건 제거")
        return text
    except Exception as e:
        print(f"[WARN] OCR 실패: {e}")
        return ""


def _active_window_title() -> str:
    if not _HAS_PYGETWINDOW:
        return "불명"
    try:
        active = gw.getActiveWindow()
        return active.title if active and active.title else "불명"
    except Exception:
        return "불명"


def _is_deep_window(window_title: str, ocr_text: str = "") -> bool:
    """코딩/집필처럼 화면 텍스트 전문을 보내야 반응이 재밌어지는 화면인지.

    창 제목만 보면 포커스가 잠깐 다른 창(설정창 등)으로 옮겨간 순간을 놓치므로,
    화면에서 읽힌 텍스트에 작업 신호가 있는지도 함께 확인한다."""
    t = window_title.lower()
    if any(k.lower() in t for k in DEEP_WINDOWS):
        return True
    o = ocr_text.lower()
    return any(k.lower() in o for k in DEEP_SIGNALS)


def _is_blocked_window(window_title: str) -> bool:
    t = window_title.lower()
    return any(k.lower() in t for k in OCR_BLOCKLIST)


def _needs_vision(window_title: str, ocr_text: str) -> bool:
    """비전 모델을 부를 가치가 있는 화면인지 판단한다.

    글자 수만으로 자르면 웹툰/트위터/유튜브 댓글처럼 '글자는 많지만 그림이 본체'인
    화면에서 장면 설명을 잃는다. 그래서 글자 수 AND 텍스트 전용 창을 함께 본다."""
    if len(ocr_text) < SKIP_VISION_MIN_OCR:
        return True
    t = window_title.lower()
    if any(k.lower() in t for k in TEXT_ONLY_WINDOWS):
        return False
    # 창 제목이 캡처 시점과 어긋났어도(캡처 도구가 포커스를 가로채는 등)
    # 집필/코딩 신호가 잡히면 텍스트 전용 화면으로 본다.
    o = ocr_text.lower()
    if any(k.lower() in o for k in DEEP_SIGNALS):
        return False
    return True


def _should_observe(window_title: str, text: str = "") -> bool:
    """자기 참조 화면과 차단 창을 제외한다. 창 제목과 화면 내용을 모두 본다."""
    if _is_blocked_window(window_title):
        return False
    t = window_title.lower()
    if any(k.lower() in t for k in OBSERVE_BLOCKLIST):
        return False
    o = text.lower()
    return not any(k.lower() in o for k in OBSERVE_CONTENT_BLOCKLIST)


def _log_observation(window: str, scene: str, ocr_brief: str) -> None:
    global _last_observation
    if not ENABLE_OBSERVATION_LOG:
        return
    if not _should_observe(window, f"{scene}\n{ocr_brief}"):
        print(f"[INFO] 관찰 제외 (자기 참조): {window}")
        return
    sig = (window, ocr_brief)
    if sig == _last_observation:
        print("[INFO] 관찰 제외 (직전과 동일)")
        return
    _last_observation = sig
    try:
        rec = {
            "t": int(time.time() * 1000),
            "win": window,
            "scene": scene,
            "ocr": ocr_brief,
        }
        with open(OBSERVATION_LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    except Exception as e:
        print(f"[WARN] 관찰 로그 기록 실패: {e}")


async def _call_vision(
    client: httpx.AsyncClient, prompt_text: str, vision_b64: str, num_predict: int
):
    """비전 모델 1회 호출. (본문, 잘림여부, thinking길이)를 반환. 실패 시 본문은 None."""
    payload = {
        "model": VISION_MODEL,
        "messages": [{"role": "user", "content": prompt_text, "images": [vision_b64]}],
        "stream": False,
        "think": False,
        "options": {
            "temperature": 0.1,
            "num_ctx": VISION_NUM_CTX,
            "num_predict": num_predict,
        },
    }
    resp = await client.post(f"{LOCAL_OLLAMA_URL}/api/chat", json=payload)
    if resp.status_code != 200:
        print(f"[ERROR] 비전 모델 응답 오류. 상태 코드: {resp.status_code} / {resp.text}")
        return None, False, 0
    result = resp.json()
    content = result["message"]["content"].strip()
    truncated = (not content) and result.get("done_reason") == "length"
    thinking_len = len(result["message"].get("thinking") or "")
    return content, truncated, thinking_len


async def extract_and_analyze_image(
    messages: List[Dict[str, Any]], window_title: Optional[str] = None
) -> Tuple[str, Optional[Dict[str, Any]]]:
    """가장 최근 이미지를 찾아 로컬 Qwen3-VL로 분석한다.

    반환: (상태, 기록)
      - ("none",    None) : 이미지가 애초에 없음 (= 말 걸기 요청)
      - ("skipped", None) : 이미지는 있지만 화면이 안 바뀜 -> 클라우드도 호출하지 말 것
      - ("ok",      dict) : 분석 완료
    """
    image_data = _find_last_image(messages)
    if not image_data:
        return "none", None
    # 캡처 시점에 본체가 읽어 보낸 제목을 우선한다. 프록시가 지금 읽으면
    # 캡처 이후 창이 바뀐 경우 차단 대상이 엉뚱한 이름표를 달고 통과한다.
    if not window_title:
        window_title = await asyncio.to_thread(_active_window_title)
    
    raw_b64 = _strip_data_uri(image_data)

    # pHash가 동일하면 비전 모델뿐 아니라 클라우드 호출까지 통째로 스킵한다.
    # (기존에는 비전만 건너뛰고 클라우드는 계속 호출돼서 정지 화면에도 요금이 나갔음)
    # 커밋(= _last_phash 갱신)은 여기서 하지 않는다. 아래에서 분석이 실제로
    # 끝난 시점에 커밋해야, 도중에 실패해도 같은 화면이 다시 오면 재시도된다.
    new_hash = await asyncio.to_thread(_compute_phash, raw_b64)
    if not _screen_changed(new_hash):
        print("[INFO] 화면 변화 없음 -> 비전 분석 + 클라우드 호출 스킵")
        return "skipped", None

    print(f"[INFO] 화면 캡처 감지됨 -> 로컬 {VISION_MODEL} 분석 시작...")

    # ---- 차단 창은 여기서 끝낸다 ----
    # OCR만 끄고 넘어가면 ocr_text가 비어서 _needs_vision()이 True가 되고,
    # OCR이 없으니 "화면 텍스트를 그대로 나열하라"는 VISION_PROMPT가 선택된다.
    # 즉 비전 모델이 OCR 대신 비밀번호 관리자 화면을 옮겨 적어 클라우드로 내보낸다.
    # 창 제목 자체도 노출 정보다 (카카오톡 제목은 대화 상대 이름이다).
    if _is_blocked_window(window_title):
        # 차단 창은 재시도해도 결론이 항상 같으므로(재분석할 내용이 없음) 커밋해서
        # 다음에 같은 화면이 오면 pHash 단계에서 바로 스킵되게 한다.
        _commit_phash(new_hash)
        if BLOCKED_SCREEN_MODE == "masked":
            print(f"[INFO] 차단 창 감지 ({window_title}) -> 껍데기 기록만 전송")
            return "ok", {
                "time": datetime.now().strftime("%H시 %M분"),
                "window": BLOCKED_TITLE_LABEL,
                "deep": False,
                "full": f"[활성 창: {BLOCKED_TITLE_LABEL}]\n(내용 비공개)",
                "brief": f"[활성 창: {BLOCKED_TITLE_LABEL}] (내용 비공개)",
            }
        print(f"[INFO] 차단 창 감지 ({window_title}) -> 비전·클라우드 모두 스킵")
        return "skipped", None

    # OCR과 다운스케일은 동기 CPU 작업이라 그대로 두면 이벤트 루프를 막는다.
    # (요청이 겹치면 줄줄이 밀림) -> 스레드로 넘긴다.
    ocr_text, vision_b64 = await asyncio.gather(
        asyncio.to_thread(_run_ocr, raw_b64, window_title),
        asyncio.to_thread(_downscale_for_vision, raw_b64),
    )

    # 창 제목 + OCR 텍스트를 함께 보고 전문/요약을 결정한다.
    deep = _is_deep_window(window_title, ocr_text)

    # ---- 비전 모델을 부를지 결정 ----
    # 원고/코드 편집기에서 OCR이 충분히 나오면 장면 설명이 덧붙일 게 없다.
    # 이 경로에서는 thinking 폭주 문제도 자동으로 회피되고 응답이 10배 빨라진다.
    vision_needed = _needs_vision(window_title, ocr_text)

    content = ""
    if not vision_needed:
        content = f"{window_title} 창에서 텍스트 작업 중"
        print(f"[INFO] 텍스트 전용 화면 (OCR {len(ocr_text)}자) -> 비전 모델 생략")
    else:
        if ocr_text:
            prompt_text = SCENE_ONLY_PROMPT + f"\n\n[OCR 텍스트]\n{ocr_text}"
            num_predict = VISION_NUM_PREDICT_SCENE
        else:
            prompt_text = VISION_PROMPT
            num_predict = VISION_NUM_PREDICT_FULL

        try:
            async with httpx.AsyncClient(timeout=VISION_TIMEOUT_SEC) as client:
                content, truncated, thinking_len = await _call_vision(
                    client, prompt_text, vision_b64, num_predict
                )
                if content is None:
                    # 비전 호출 자체가 실패(HTTP 오류 등). 여기서 통째로 포기하면
                    # 이미 뽑아둔 ocr_text까지 버려진다. OCR만으로 계속 진행하고,
                    # 정말 아무것도 없을 때만 아래에서 "(장면 설명 없음)"으로 대체된다.
                    content = ""

                # think:False를 줘도 Qwen3-VL이 thinking을 뱉는 경우가 있고, 예산을 올리면
                # thinking도 같이 늘어나서(실측 954 -> 1004 -> 1936자) 숫자로는 해결되지 않는다.
                # 프롬프트를 극단적으로 짧게 줘서 추론 자체를 억제하는 쪽으로 재시도한다.
                if truncated:
                    print(f"[WARN] thinking({thinking_len}자) 폭주로 본문 잘림 -> 짧은 프롬프트로 재시도")
                    content, truncated2, _ = await _call_vision(
                        client, VISION_RETRY_PROMPT, vision_b64, VISION_NUM_PREDICT_RETRY
                    )
                    if content is None:
                        content = ""
                    if truncated2:
                        print("[WARN] 재시도도 실패 -> 장면 설명 없이 진행")
        except Exception as e:
            print(f"[ERROR] 비전 모델 호출 실패: {e}")
            content = ""  # OCR만으로 계속 진행

    # ---- 클라우드로 나갈 텍스트 예산 적용 ----
    # 창 종류에 따라 전문(4000자) / 요약(200자)을 나눈다. 여기가 요금의 대부분을 결정한다.
    budget = OCR_BUDGET_DEEP if deep else OCR_BUDGET_NORMAL
    ocr_sized = ocr_text[:budget]
    ocr_brief = " ".join(ocr_text[:OCR_BRIEF_LEN].split())

    scene = content or "(장면 설명 없음)"
    # brief는 context.py에서 "- [시간] ..." 한 줄짜리 목록 항목으로 쓰인다.
    # scene은 최대 3문장짜리라 줄바꿈이 섞여 있으면 그 목록 형식이 깨진다.
    scene_brief = " ".join(scene.split())
    if len(scene_brief) > SCENE_BRIEF_LEN:
        scene_brief = scene_brief[:SCENE_BRIEF_LEN].rstrip() + "…"

    if ocr_text:
        full_body = f"[텍스트]\n{ocr_sized}\n\n[장면]\n{scene}"
        # 공백으로만 이어붙여 한 줄을 유지한다 ([텍스트요약]/[장면] 사이도 개행 금지).
        brief_body = f"[텍스트요약] {ocr_brief} [장면] {scene_brief}"
    else:
        full_body = scene
        brief_body = scene_brief

    record = {
        "time": datetime.now().strftime("%H시 %M분"),
        "window": window_title,
        "deep": deep,
        "full": f"[활성 창: {window_title}]\n{full_body}",
        "brief": f"[활성 창: {window_title}] {brief_body}",
    }

    if deep:
        by_title = any(k.lower() in window_title.lower() for k in DEEP_WINDOWS)
        reason = "창제목" if by_title else "OCR신호"
        mode = f"전문({reason})"
    else:
        mode = "요약"
    _, crop_profile = _crop_box(window_title)
    print(
        f"[SUCCESS] 화면 분석 완료 (창={window_title} / {mode} "
        f"{len(ocr_sized)}자, OCR원본 {len(ocr_text)}자, 크롭={crop_profile}, "
        f"비전={'ON' if vision_needed else 'SKIP'})\n{record['full'][:300]}"
    )

    _commit_phash(new_hash)
    _log_observation(window_title, scene, ocr_brief)
    return "ok", record
