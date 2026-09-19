"""미진이의 기억 저장소.

파일 다섯으로 나눠 쌓는다:
    mem/chat_history.jsonl   최근 대화 (프롬프트에 실림)
    mem/chat_archive.jsonl   밀려난 대화 (프롬프트엔 안 실림, 뷰어·검색용)
    mem/monologue.jsonl      혼잣말 (최근 몇 개만 프롬프트에 실림)
    mem/events.jsonl         찌르기·블라인드 등 클라이언트가 만든 지문 (최근 몇 개만 프롬프트에 실림)
    mem/episodes.jsonl       날짜별 요약 + 키워드

대화·혼잣말·이벤트를 나누는 이유: 한 목록에 섞으면 훨씬 잦은 혼잣말/이벤트가 사용자와
나눈 대화를 금방 밀어낸다 (혼잣말만으로 반나절이면 참). 이벤트는 사람이 친 말이 아니라
클라이언트가 만든 지문이라, 대화와 섞이면 대화 예산(MIJIN_MAX_CHAT_TURNS)을 나눠 먹고
"찌르면 이렇게 답한다"는 반응이 그대로 학습되듯 반복된다.

레코드 형식은 viewer.py가 읽는 것과 맞췄다 ("t" 밀리초, "role", "text").
role: user | mijin | self(혼잣말) | event
"""

import json
import time
from pathlib import Path
from typing import Any, Dict, List

from config import (
    MEM_DIR, MIJIN_MAX_CHAT_TURNS, MIJIN_MAX_MONOLOGUE_TURNS, MIJIN_MAX_EVENT_TURNS,
    MONOLOGUE_MARK, EPISODE_INJECT_COUNT,
)

_DIR = Path(MEM_DIR)
CHAT_FILE = _DIR / "chat_history.jsonl"
ARCHIVE_FILE = _DIR / "chat_archive.jsonl"
MONO_FILE = _DIR / "monologue.jsonl"
EVENT_FILE = _DIR / "events.jsonl"
EPISODE_FILE = _DIR / "episodes.jsonl"

# 프롬프트에 실을 최근 기록만 메모리에 들고 있는다.
# 아카이브는 읽지 않는다 (나중에 검색으로 꺼내 쓸 원료).
_chat: List[Dict[str, Any]] = []
_mono: List[Dict[str, Any]] = []
_events: List[Dict[str, Any]] = []


# ───────────────── 파일 입출력 ─────────────────

def _read_jsonl(path: Path) -> List[Dict[str, Any]]:
    """읽기 전용. 쓰는 도중이라 마지막 줄이 깨져 있을 수 있으므로 건너뛴다."""
    if not path.exists():
        return []
    out = []
    try:
        with path.open(encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    out.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    except OSError as e:
        print(f"[MEM] {path.name} 읽기 실패: {e}")
    return out


def _append_jsonl(path: Path, recs: List[Dict[str, Any]]) -> None:
    try:
        _DIR.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as f:
            for r in recs:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
    except OSError as e:
        print(f"[MEM] {path.name} 기록 실패: {e}")


def _rewrite_jsonl(path: Path, recs: List[Dict[str, Any]]) -> None:
    """파일을 통째로 다시 쓴다. 회전(오래된 것 덜어내기) 때만 부른다."""
    try:
        _DIR.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        with tmp.open("w", encoding="utf-8") as f:
            for r in recs:
                f.write(json.dumps(r, ensure_ascii=False) + "\n")
        tmp.replace(path)   # 도중에 죽어도 원본이 날아가지 않게
    except OSError as e:
        print(f"[MEM] {path.name} 재작성 실패: {e}")


# ───────────────── 시작 시 불러오기 ─────────────────

def load() -> None:
    """프록시 시작 때 한 번 부른다. 재시작해도 기억이 이어진다."""
    global _chat, _mono, _events
    _chat = _read_jsonl(CHAT_FILE)
    _mono = _read_jsonl(MONO_FILE)[-MIJIN_MAX_MONOLOGUE_TURNS:]
    _events = _read_jsonl(EVENT_FILE)[-MIJIN_MAX_EVENT_TURNS * 2:]
    eps = len(_read_jsonl(EPISODE_FILE))
    print(f"[MEM] 대화 {len(_chat)//2}턴 / 혼잣말 {len(_mono)}건 / 이벤트 {len(_events)//2}건 / 회고 {eps}일치 불러옴")


# ───────────────── 기록하기 ─────────────────

def add_chat(user_text: str, line: str) -> None:
    """사용자와 주고받은 한 턴을 남긴다."""
    now = int(time.time() * 1000)
    recs = [
        {"t": now, "role": "user", "text": user_text},
        {"t": now + 1, "role": "mijin", "text": line},
    ]
    _chat.extend(recs)
    _append_jsonl(CHAT_FILE, recs)
    _rotate_chat()


def add_monologue(line: str) -> None:
    """혼잣말 한 건을 남긴다.

    사용자 발화 쪽은 매번 똑같은 지시문이라 저장하지 않는다.
    프롬프트를 조립할 때 MONOLOGUE_MARK로 되살린다.
    """
    rec = {"t": int(time.time() * 1000), "role": "self", "text": line}
    _mono.append(rec)
    _append_jsonl(MONO_FILE, [rec])
    if len(_mono) > MIJIN_MAX_MONOLOGUE_TURNS:
        del _mono[:len(_mono) - MIJIN_MAX_MONOLOGUE_TURNS]
    # 혼잣말 파일은 회전하지 않는다. 뷰어에서 하루를 통째로 돌아보는 재미가
    # 있고, 프롬프트에는 메모리의 최근 몇 건만 쓰므로 길어져도 비용이 안 든다.


def add_event(event_text: str, line: str) -> None:
    """찌르기·블라인드 등 클라이언트가 만든 지문 한 턴을 남긴다.

    혼잣말과 달리 지문 쪽 텍스트가 매번 다르므로(연속 횟수 등) 대화와 같은
    2레코드 형태로 저장한다. 상한을 넘으면 메모리에서만 잘라내고, 파일은
    회전하지 않는다 (혼잣말과 같은 이유 - 뷰어용 하루 기록, 프롬프트 비용과 무관).
    """
    now = int(time.time() * 1000)
    recs = [
        {"t": now, "role": "event", "text": event_text},
        {"t": now + 1, "role": "mijin", "text": line},
    ]
    _events.extend(recs)
    _append_jsonl(EVENT_FILE, recs)
    cap = MIJIN_MAX_EVENT_TURNS * 2
    if len(_events) > cap:
        del _events[:len(_events) - cap]


def _rotate_chat() -> None:
    """최근 N턴만 남기고 나머지는 아카이브로 옮긴다."""
    cap = MIJIN_MAX_CHAT_TURNS * 2
    if len(_chat) <= cap:
        return
    overflow = _chat[:len(_chat) - cap]
    del _chat[:len(_chat) - cap]
    _append_jsonl(ARCHIVE_FILE, overflow)
    _rewrite_jsonl(CHAT_FILE, _chat)
    print(f"[MEM] 대화 {len(overflow)//2}턴을 아카이브로 옮김")


def add_episode(date: str, summary: str, keywords: List[str]) -> None:
    """하루치 회고를 남긴다. 하루 한 번만 부를 것 (자주 부르면 캐시가 깨진다)."""
    _append_jsonl(EPISODE_FILE, [{
        "date": date,
        "summary": summary,
        "keywords": keywords,
        "t": int(time.time() * 1000),
    }])


def has_episode(date: str) -> bool:
    return any(r.get("date") == date for r in _read_jsonl(EPISODE_FILE))


def archive_for_date(date: str) -> List[Dict[str, Any]]:
    """그날의 대화 전체 (아카이브 + 현재 기록). 회고를 만들 원료로 쓴다."""
    import datetime
    out = []
    for r in _read_jsonl(ARCHIVE_FILE) + _chat:
        t = r.get("t")
        if not t:
            continue
        if datetime.datetime.fromtimestamp(t / 1000).strftime("%Y-%m-%d") == date:
            out.append(r)
    out.sort(key=lambda r: r["t"])
    return out


def events_for_date(date: str) -> List[Dict[str, Any]]:
    """그날의 이벤트 전체 (찌르기·블라인드 등). 회고를 만들 원료로 쓴다.

    이벤트 파일은 회전하지 않으므로(add_event 참고) 파일을 통째로 읽는다."""
    import datetime
    out = []
    for r in _read_jsonl(EVENT_FILE):
        t = r.get("t")
        if not t:
            continue
        if datetime.datetime.fromtimestamp(t / 1000).strftime("%Y-%m-%d") == date:
            out.append(r)
    out.sort(key=lambda r: r["t"])
    return out


# ───────────────── 프롬프트에 실을 형태로 ─────────────────

def build_history() -> List[Dict[str, str]]:
    """대화·혼잣말·이벤트를 시간순으로 합쳐 메시지 목록으로 만든다."""
    merged: List[Dict[str, Any]] = []

    for r in _chat:
        role = "assistant" if r.get("role") == "mijin" else "user"
        merged.append({"t": r.get("t", 0), "role": role, "content": r.get("text", "")})

    for r in _mono:
        t = r.get("t", 0)
        merged.append({"t": t - 1, "role": "user", "content": MONOLOGUE_MARK})
        merged.append({"t": t, "role": "assistant", "content": r.get("text", "")})

    for r in _events:
        role = "assistant" if r.get("role") == "mijin" else "user"
        merged.append({"t": r.get("t", 0), "role": role, "content": r.get("text", "")})

    merged.sort(key=lambda m: m["t"])
    return [{"role": m["role"], "content": m["content"]} for m in merged]


def episode_block() -> str:
    """최근 회고 몇 개를 시스템 메시지에 붙일 텍스트로 만든다.

    하루 한 번만 바뀌므로 시스템 메시지 뒤에 붙여도 프롬프트 캐시가 거의 유지된다.
    """
    eps = _read_jsonl(EPISODE_FILE)[-EPISODE_INJECT_COUNT:]
    if not eps:
        return ""
    lines = [f"- {e.get('date', '')}: {e.get('summary', '')}" for e in eps]
    return "\n\n[지난 며칠의 기억]\n" + "\n".join(lines)


# ───────────────── 비우기 ─────────────────

def reset() -> Dict[str, int]:
    """프롬프트에 실리는 기록을 비운다.

    지우지 않고 아카이브로 옮긴다. 페르소나를 고치며 시험하다가
    진짜 대화까지 날리는 일이 없도록.
    """
    moved = len(_chat)
    if _chat:
        _append_jsonl(ARCHIVE_FILE, _chat)
        _chat.clear()
        _rewrite_jsonl(CHAT_FILE, [])
    _mono.clear()
    _events.clear()
    return {"archived": moved // 2}
