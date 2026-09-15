"""클라우드로 보낼 메시지에 정보를 주입한다 (정보 주입).

vision.py가 만든 화면 분석 기록을 단기 기억으로 쌓고, profile.txt 같은
정적 사용자 정보를 시스템 메시지에 붙인다. proxy_server.py에서 쓰는
진입점은 clean_messages() 하나다.
"""

import os
import time
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

from config import (
    MAX_SCREEN_MEMORY, SCREEN_MEMORY_TTL_SEC, LATEST_FULL_SEC,
    PROFILE_FILES, PROFILE_MAX_CHARS,
    DEEP_HINT, PERSONA_REMINDER,
)

# 각 항목: {"time": "14시 23분", "full": "...", "brief": "...", "deep": bool}
# 최신 1개만 full로 전송하고 과거는 brief로 압축한다 (같은 전문이 3번 중복 전송되는 것 방지).
global_screen_memory: List[Dict[str, Any]] = []

_profile_cache = {"key": None, "text": ""}


def _profile_stat_key() -> tuple:
    """파일이 바뀌었는지만 보는 열쇠. 매 요청 stat 두 번은 무시할 만한 비용이다."""
    key = []
    for path, _label in PROFILE_FILES:
        try:
            st = os.stat(path)
            key.append((path, st.st_mtime_ns, st.st_size))
        except OSError:
            key.append((path, None, None))
    return tuple(key)


def _load_profile() -> str:
    """프로필 파일을 읽어 주입할 텍스트를 만든다. 파일이 바뀔 때만 다시 읽으므로
    프록시를 재시작하지 않아도 저장하는 즉시 다음 요청에 반영된다.

    파일별로 라벨을 붙여서 섞이지 않게 한다 - profile.txt(손으로 쓴 확정 사실)와
    distilled_profile.txt(나중에 증류기가 관찰 로그에서 뽑아낼 추정치)를 같은
    신뢰도로 주입하면, 화면에 우연히 뜬 문장이나 로컬 모델의 오추론이 그대로
    "사용자에 대한 확정 사실"로 캐릭터에 박힐 수 있다."""
    key = _profile_stat_key()
    if key == _profile_cache["key"]:
        return _profile_cache["text"]

    all_lines: List[str] = []
    for path, label in PROFILE_FILES:
        try:
            with open(path, encoding="utf-8") as f:
                content_lines = [
                    line.strip() for line in f
                    if line.strip() and not line.lstrip().startswith("#")
                ]
        except FileNotFoundError:
            continue
        except OSError as e:
            print(f"[WARN] 프로필 읽기 실패 ({path}): {e}")
            continue
        if not content_lines:
            continue
        if all_lines:
            all_lines.append("")
        all_lines.append(label)
        all_lines.extend(content_lines)

    # PROFILE_MAX_CHARS로 자를 때 줄 중간을 끊으면 문장이 깨진 채로 주입된다.
    # 줄 단위로만 자르고, 잘렸으면 알 수 있게 로그를 남긴다.
    kept: List[str] = []
    used = 0
    for i, line in enumerate(all_lines):
        added = len(line) + (1 if kept else 0)  # 줄바꿈 몫
        if used + added > PROFILE_MAX_CHARS:
            print(f"[WARN] 프로필이 {PROFILE_MAX_CHARS}자 제한을 넘어 {len(all_lines) - i}줄이 잘렸습니다")
            break
        kept.append(line)
        used += added

    text = "\n".join(kept)
    if text:
        text = "\n\n" + text
    _profile_cache["key"] = key
    _profile_cache["text"] = text
    print(f"[INFO] 프로필 갱신: {len(text)}자")
    return text


def _inject_profile(cleaned: List[Dict[str, Any]]) -> None:
    """시스템 메시지 끝에 붙인다. 화면 기억(마지막 user 메시지)과 달리 잘 안 바뀌므로
    앞부분 프롬프트 캐시에 얹혀 캐시 입력 단가로 계산된다."""
    profile = _load_profile()
    if not profile:
        return
    for msg in cleaned:
        if msg.get("role") == "system":
            msg["content"] = (msg.get("content") or "") + profile
            return
    # 모드가 시스템 메시지를 안 보내는 경우 맨 앞에 하나 만든다.
    cleaned.insert(0, {"role": "system", "content": profile.strip()})


def _build_screen_memory(now: float, has_new_screen: bool) -> Tuple[str, bool]:
    """화면 기억을 조립한다. 최신 1개만(그것도 충분히 최근일 때만) 전문, 나머지는 압축본.

    TTL이 지난 기억은 아예 주입하지 않는다 - "time" 문자열엔 날짜가 없어 모델이
    스스로 얼마나 오래됐는지 판단할 수 없으므로, 자리를 비웠다가 돌아와도 옛 화면을
    "지금 보고 있는 것"처럼 반응하게 된다. 최신 항목도 has_new_screen(이번 턴에 실제로
    캡처됨)이 아니고 LATEST_FULL_SEC보다 오래됐으면 전문 대신 압축본으로 낮춘다 -
    안 그러면 "이거 어때?" 같은 말 걸기 요청마다 최대 4000자짜리 화면 전문이 매번
    다시 나가고 DEEP_HINT까지 붙어 캐릭터가 계속 그 화면 얘기로 돌아가게 된다.

    반환: (주입할 텍스트, 최신 항목을 전문으로 보냈고 그게 작업 창인지 여부)
    """
    fresh = [r for r in global_screen_memory if now - r["ts"] < SCREEN_MEMORY_TTL_SEC]
    if not fresh:
        return "", False

    *older, latest = fresh
    lines = [f"- [{o['time']}] {o['brief']}" for o in older]

    latest_is_recent = has_new_screen or (now - latest["ts"] < LATEST_FULL_SEC)
    lines.append(f"- [{latest['time']}] {latest['full'] if latest_is_recent else latest['brief']}")

    now_str = datetime.now().strftime("%H시 %M분")
    text = f"\n[시스템 기억 - 최근 사용자의 화면 변경 기록 / 현재 시각 {now_str}]\n" + "\n".join(lines) + "\n"
    return text, bool(latest.get("deep")) and latest_is_recent


def clean_messages(
    messages: List[Dict[str, Any]], screen_record: Optional[Dict[str, Any]]
) -> List[Dict[str, Any]]:
    """이미지 데이터를 제거하고 화면 기억과 리마인더를 주입합니다."""
    global global_screen_memory

    # 1. 새 화면 정보가 있으면 기억 장치에 저장
    now = time.time()
    if screen_record:
        screen_record["ts"] = now
        global_screen_memory.append(screen_record)
        # 기억이 너무 길어지면 가장 오래된 것 삭제
        if len(global_screen_memory) > MAX_SCREEN_MEMORY:
            global_screen_memory.pop(0)

    # 2. 메시지 정제 (클라우드로 갈 이미지 데이터 텍스트화)
    cleaned = []
    for msg in messages:
        content = msg.get("content")

        if isinstance(content, list):
            # 텍스트 파트가 여러 개면 구분자 없이 이어 붙이면 단어가 들러붙을 수 있다.
            text_parts = [
                part.get("text", "") for part in content
                if isinstance(part, dict) and part.get("type") == "text"
            ]
            text_content = "\n".join(text_parts)
        elif isinstance(content, str):
            text_content = content
        else:
            text_content = ""

        if not text_content and content:
            # 이미지만 있던 과거 user 메시지 (텍스트 파트 없음). 빈 content를 거부하는
            # 프로바이더도 있고 턴 순서 해석도 애매해지므로 자리표시자를 넣는다.
            text_content = "(화면 캡처)"

        # role/content만 남기면 tool_calls, tool_call_id 같은 필드가 조용히 사라진다.
        # 지금은 안 쓰지만, 나중에 모드가 tool calling을 붙이면 여기서 깨지게 된다.
        cleaned.append({**msg, "content": text_content})

    # 3. 프로필 주입 (시스템 메시지 = 캐시되는 앞부분)
    _inject_profile(cleaned)

    # 4. 과거 화면 기억 조립 (최신만 전문, 나머지는 압축)
    injection_text, latest_is_deep = _build_screen_memory(now, has_new_screen=bool(screen_record))
    if injection_text and latest_is_deep:
        injection_text += DEEP_HINT
    # 화면 기억이 없을 때(말 걸기 요청)도 말투·호칭은 유지되어야 하므로 항상 붙인다.
    injection_text += PERSONA_REMINDER

    # 5. 마지막 사용자 메시지에 기억과 리마인더 강제 주입
    #    (시스템 프롬프트가 아니라 여기에 넣어야 앞부분 프롬프트 캐시가 유지된다)
    if cleaned and injection_text:
        for i in range(len(cleaned) - 1, -1, -1):
            if cleaned[i]["role"] == "user":
                original_text = cleaned[i]["content"]
                cleaned[i]["content"] = f"{injection_text}\n{original_text}"
                break

    return cleaned
