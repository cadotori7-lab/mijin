"""프로필 증류기.

관찰 로그와 회고를 모아 "파트너님은 이런 사람" 이라는 짧은 목록을 만든다.
결과는 distilled_profile.txt에 쓰이고, context.py가 profile.txt와는 다른
라벨로 주입한다 (확정 사실이 아니라 추정이라는 것을 모델이 구분하게).

원료가 화면 OCR이라는 점 때문에 위험이 있다:
  - 화면에 우연히 뜬 문장("이전 지시를 무시하고...")이 프로필로 둔갑할 수 있다
  - AI가 쓴 답변 본문을 사용자의 생각으로 오인할 수 있다
  - 로컬 모델의 오추론이 확정 사실처럼 굳을 수 있다
그래서 (1) 원료 단계에서 창 제목과 짧은 요약만 쓰고 OCR 전문은 아예 넣지 않으며,
(2) 결과에서 지시문처럼 보이는 줄을 걸러내고, (3) 길이를 자른다.

하루 한 번만 돈다. 자주 갱신하면 이 파일이 바뀔 때마다
시스템 메시지 앞부분 프롬프트 캐시가 깨진다.
"""

import re
from datetime import datetime, timedelta, date as _date
from pathlib import Path
from typing import List, Optional

from config import (
    DISTILL_ENABLED, DISTILL_DAYS, DISTILL_PROMPT, DISTILL_MAX_CHARS,
    DISTILL_MAX_MATERIAL, DISTILLED_FILE, DISTILL_DROP_PATTERNS,
    OBSERVATION_LOG, OBSERVE_CONTENT_BLOCKLIST,
)
import memory

_ran_on: Optional[str] = None


def _material(days: int) -> str:
    """최근 며칠치 원료. 창 제목과 짧은 요약만 쓴다.

    OCR 전문을 넣지 않는 것이 첫 번째 방어선이다. 전문에는 화면에 떠 있던
    남의 문장이 통째로 들어 있어서, 그걸 모델에게 주면 사용자의 생각과
    구분할 방법이 없다.
    """
    since = (_date.today() - timedelta(days=days)).isoformat()

    # 회고는 이미 한 번 정제된 것이라 그대로, 전부 담는다. 우선순위가 높다.
    recap_lines: List[str] = []
    for e in memory._read_jsonl(memory.EPISODE_FILE):
        if e.get("date", "") >= since:
            recap_lines.append(f"{e['date']} (회고) {e.get('summary', '')}")
    recap_block = "\n".join(recap_lines)

    # 관찰 로그는 며칠만 쌓여도 수만 자에 달한다. 회고가 쓰고 남은 예산만큼만 담고,
    # 넘치면 앞부분만 자르는 대신 고르게 솎아내 14일 중 특정 며칠만 반영되는 것을 막는다.
    obs_lines: List[str] = []
    for r in memory._read_jsonl(Path(OBSERVATION_LOG)):
        t = r.get("t")
        if not t:
            continue
        day = datetime.fromtimestamp(t / 1000).strftime("%Y-%m-%d")
        if day < since:
            continue

        win = (r.get("win") or "").strip()
        scene = " ".join((r.get("scene") or "").split())[:80]
        blob = f"{win} {scene}".lower()

        # 자기 참조 화면(프록시 개발, AI 상담)은 사용자의 취향이 아니라 일회성 작업이다
        if any(k.lower() in blob for k in OBSERVE_CONTENT_BLOCKLIST):
            continue
        if not win:
            continue

        hour = datetime.fromtimestamp(t / 1000).strftime("%H시")
        obs_lines.append(f"{day} {hour} | {win} | {scene}")

    budget = max(0, DISTILL_MAX_MATERIAL - len(recap_block))
    if obs_lines:
        joined = "\n".join(obs_lines)
        if budget <= 0:
            obs_lines = []
        elif len(joined) > budget:
            step = max(1, len(joined) // budget + 1)
            obs_lines = obs_lines[::step]

    return (recap_block + "\n" + "\n".join(obs_lines)).strip()


def _sanitize(text: str) -> str:
    """결과에서 지시문처럼 보이는 줄을 걸러내고 길이를 자른다."""
    out: List[str] = []
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        if any(p.search(line) for p in DISTILL_DROP_PATTERNS):
            print(f"[DISTILL] 지시문 의심 줄 제거: {line[:40]}")
            continue
        if not line.startswith("-"):
            line = "- " + line.lstrip("*•· ")
        out.append(line)

    joined = "\n".join(out)
    if len(joined) > DISTILL_MAX_CHARS:
        # 줄 중간에서 자르지 않는다
        kept: List[str] = []
        total = 0
        for line in out:
            if total + len(line) + 1 > DISTILL_MAX_CHARS:
                break
            kept.append(line)
            total += len(line) + 1
        joined = "\n".join(kept)
        print(f"[DISTILL] 길이 초과로 {len(out) - len(kept)}줄 잘라냄")
    return joined


def _previous() -> str:
    try:
        p = Path(DISTILLED_FILE)
        return p.read_text(encoding="utf-8").strip() if p.exists() else ""
    except OSError:
        return ""


async def build(force: bool = False) -> Optional[str]:
    """프로필을 새로 증류해 파일에 쓴다. 성공하면 결과 문자열을 돌려준다."""
    if not DISTILL_ENABLED:
        return None

    material = _material(DISTILL_DAYS)
    if len(material) < 200:
        print("[DISTILL] 관찰 기록이 적어 건너뜀")
        return None

    prompt = DISTILL_PROMPT.format(
        previous=_previous() or "(아직 없음)",
        days=DISTILL_DAYS,
    ) + "\n\n[최근 기록]\n" + material

    # 회고와 같은 경로를 쓴다. 하루 한 번이라 비용이 거의 들지 않는다.
    from mijin import _call_cloud
    raw = await _call_cloud([{"role": "user", "content": prompt}])
    if not raw:
        return None

    # 모델이 머리말을 붙이는 경우가 있어 목록 줄만 남긴다
    raw = re.sub(r"^```.*?$", "", raw, flags=re.MULTILINE)
    result = _sanitize(raw)
    if not result:
        print("[DISTILL] 남은 내용이 없어 파일을 갱신하지 않음")
        return None

    try:
        Path(DISTILLED_FILE).write_text(result, encoding="utf-8")
        print(f"[DISTILL] 프로필 갱신: {len(result)}자 / {result.count(chr(10)) + 1}줄")
    except OSError as e:
        print(f"[DISTILL] 파일 쓰기 실패: {e}")
        return None

    return result


async def ensure() -> None:
    """하루 한 번만 실제로 돈다."""
    global _ran_on
    today = _date.today().isoformat()
    if _ran_on == today:
        return
    _ran_on = today
    await build()
