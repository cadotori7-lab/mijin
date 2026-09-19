"""하루치 회고 만들기.

그날의 대화 + 혼잣말 + 화면 관찰을 모아 클라우드 페르소나 모델(mijin.py가 대사를
짓는 그 모델)에게 미진이 시점의 일기를 쓰게 한다. 하루 한 번뿐이라 비용이 거의
안 들고, 같은 모델이라 회고 문체도 평소 대사와 자연히 맞는다.

언제 도는가:
  - 요청이 들어올 때마다 ensure_recent()가 어제 회고가 있는지 확인하고 없으면 만든다.
    (프록시가 며칠 꺼져 있었어도 다음에 켤 때 밀린 날짜가 채워진다)
  - POST /mijin/episode 로 손수 부를 수도 있다 (확인용).
"""

import json
import re
from datetime import datetime, timedelta, date as _date
from pathlib import Path
from typing import Any, Dict, List, Optional

from config import (
    EPISODE_PROMPT, EPISODE_MAX_MATERIAL, EPISODE_BACKFILL_DAYS,
    EPISODE_THINKING, OBSERVATION_LOG,
)
import memory

# 오늘 이미 확인했으면 다시 확인하지 않는다 (매 요청 파일을 뒤지지 않게).
_checked_on: Optional[str] = None


def _observations_for_date(date: str) -> List[Dict[str, Any]]:
    out = []
    for r in memory._read_jsonl(Path(OBSERVATION_LOG)):
        t = r.get("t")
        if not t:
            continue
        if datetime.fromtimestamp(t / 1000).strftime("%Y-%m-%d") == date:
            out.append(r)
    return out


def _collect(date: str) -> str:
    """그날의 원료를 시간순 한 덩어리로 만든다.

    대화가 가장 중요하므로 먼저 담고, 남는 예산만큼 관찰을 담는다.
    관찰은 수백 건씩 쌓여서 그대로 넣으면 대화가 묻힌다.
    """
    lines: List[str] = []

    for r in memory.archive_for_date(date):
        who = "파트너님" if r.get("role") == "user" else "나"
        t = datetime.fromtimestamp(r["t"] / 1000).strftime("%H:%M")
        lines.append(f"[{t}] {who}: {r.get('text', '')}")

    # 이벤트(찌르기·블라인드 등)도 원료에 넣는다 - 일기 성격에 맞고, 하루 한 번뿐이라
    # 비용이 거의 없다. 대화 다음·관찰보다 앞에 둬서 대화 우선 원칙을 유지한다.
    for r in memory.events_for_date(date):
        who = "파트너님" if r.get("role") == "event" else "나"
        t = datetime.fromtimestamp(r["t"] / 1000).strftime("%H:%M")
        lines.append(f"[{t}] {who}: {r.get('text', '')}")

    chat_block = "\n".join(lines)
    budget = max(0, EPISODE_MAX_MATERIAL - len(chat_block))

    obs_lines: List[str] = []
    for r in _observations_for_date(date):
        t = datetime.fromtimestamp(r["t"] / 1000).strftime("%H:%M")
        scene = " ".join((r.get("scene") or "").split())[:120]
        win = r.get("win", "")
        obs_lines.append(f"[{t}] (내가 본 화면) {win} — {scene}")

    # 관찰이 예산을 넘으면 고르게 솎아낸다. 앞부분만 자르면 하루의 뒷부분이 통째로 사라진다.
    if obs_lines:
        joined = "\n".join(obs_lines)
        if len(joined) > budget and budget > 0:
            step = max(1, len(joined) // budget + 1)
            obs_lines = obs_lines[::step]
        elif budget <= 0:
            obs_lines = []

    return (chat_block + "\n" + "\n".join(obs_lines)).strip()


async def _summarize(date: str, material: str) -> Optional[Dict[str, Any]]:
    """클라우드 페르소나 모델(TARGET_MODEL)을 그대로 재사용한다. 하루 한 번뿐이라
    비용이 거의 안 들고, 평소 대사를 짓는 모델과 같아 회고 문체도 일관된다.

    mijin이 이미 이 모듈을 최상단에서 임포트하고 있어 여기서 mijin을 최상단에서
    되받으면 순환 임포트가 난다 - 함수 안에서 늦게 임포트해 피한다."""
    from mijin import _call_cloud

    prompt = EPISODE_PROMPT.format(date=date) + "\n\n[오늘의 기록]\n" + material

    messages = []
    if EPISODE_THINKING:
        messages.append({"role": "system", "content": "<|think|>"})
    messages.append({"role": "user", "content": prompt})

    raw = await _call_cloud(messages)
    if not raw:
        print("[EPISODE] 요약 실패: 클라우드 응답 없음")
        return None
    raw = raw.strip()

    # 모델이 ```json 울타리를 붙이는 경우가 잦다
    raw = re.sub(r"^```(?:json)?|```$", "", raw, flags=re.MULTILINE).strip()
    m = re.search(r"\{.*\}", raw, re.DOTALL)
    if not m:
        print(f"[EPISODE] JSON을 못 찾음: {raw[:120]}")
        return None

    try:
        data = json.loads(m.group())
    except json.JSONDecodeError as e:
        print(f"[EPISODE] JSON 파싱 실패: {e}")
        return None

    summary = str(data.get("summary", "")).strip()
    if not summary:
        return None

    kws = data.get("keywords") or []
    if isinstance(kws, str):
        kws = [k.strip() for k in kws.split(",")]
    kws = [str(k).strip() for k in kws if str(k).strip()][:6]

    return {"summary": summary, "keywords": kws}



async def build(date: str, force: bool = False) -> Optional[Dict[str, Any]]:
    """하루치 회고를 만들어 저장한다. 이미 있으면 건너뛴다."""
    if not force and memory.has_episode(date):
        return None

    material = _collect(date)
    if len(material) < 100:
        print(f"[EPISODE] {date}: 기록이 적어 회고를 건너뜀")
        return None

    result = await _summarize(date, material)
    if not result:
        return None

    memory.add_episode(date, result["summary"], result["keywords"])
    print(f"[EPISODE] {date} 회고 작성: {result['summary'][:60]}")
    return result


async def ensure_recent() -> None:
    """어제부터 며칠치까지 빠진 회고를 채운다. 하루에 한 번만 실제로 확인한다."""
    global _checked_on

    today = _date.today().isoformat()
    if _checked_on == today:
        return
    _checked_on = today

    for back in range(1, EPISODE_BACKFILL_DAYS + 1):
        day = (_date.today() - timedelta(days=back)).isoformat()
        if not memory.has_episode(day):
            await build(day)
