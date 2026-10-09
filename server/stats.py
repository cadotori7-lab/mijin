"""요청 계측 (요청 하나당 한 줄) + /stats 집계 뷰어.

mem/ 과 다른 성격이다 - 이건 기억이 아니라 계측이라 STATS_FILE을 mem/ 밖에
둔다 (/mijin/reset이나 회고가 건드리면 안 된다).

기록에는 숫자와 분류만 담는다. 창 제목·OCR 텍스트·장면 설명·대사·사용자
발화는 절대 넣지 않는다 - 집계 화면이 127.0.0.1이라도 파일은 디스크에
평문으로 남는다. 창 종류가 필요하면 deep(작업창 여부)과 path로 충분하다.

"skipped"가 세 가지 다른 일(의도한 절약 / 프라이버시 차단 / 장애)을 뜻하므로,
outcome과 path를 분리해서 본다 - 장애가 절약으로 둔갑하면 안 된다.
"""

import json
import math
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

from fastapi import APIRouter
from fastapi.responses import HTMLResponse, JSONResponse

from config import (
    STATS_FILE, PRICE_CHECKED, PRICE_INPUT, PRICE_CACHED_INPUT,
    PRICE_OUTPUT, PRICE_PEAK_MULTIPLIER,
)

router = APIRouter(prefix="/stats")


# ───────────────── 기록 ─────────────────

def record(rec: Dict[str, Any]) -> None:
    """한 줄 append. 실패해도 요청을 막지 않는다 - 콘솔에 경고만."""
    try:
        path = Path(STATS_FILE)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    except OSError as e:
        print(f"[STATS] 기록 실패: {e}")


def _read(days: int) -> List[Dict[str, Any]]:
    """읽기 전용. 쓰는 도중이라 마지막 줄이 깨져 있을 수 있으므로 건너뛴다."""
    path = Path(STATS_FILE)
    if not path.exists():
        return []
    cutoff = int(datetime.now().timestamp() * 1000) - days * 86400000
    out: List[Dict[str, Any]] = []
    try:
        with path.open(encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    r = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if r.get("t", 0) >= cutoff:
                    out.append(r)
    except OSError as e:
        print(f"[STATS] 읽기 실패: {e}")
    return out


# ───────────────── 집계 도구 ─────────────────

def pct(values: List[float], p: float) -> Optional[float]:
    """nearest-rank 백분위. 빈 목록이면 None.

    pct([1..10], 50) == 5, pct([1..10], 95) == 10, pct([4210], 95) == 4210
    """
    if not values:
        return None
    s = sorted(values)
    k = max(1, math.ceil(p / 100 * len(s)))
    return s[k - 1]


def is_peak(t_ms: int) -> bool:
    """평일 12:00~18:00 UTC. 반드시 UTC 요일로 판단한다.

    한국 시각 평일로 판단하면 토요일 새벽 1시(KST, UTC로는 금요일 피크 시간대)를
    놓친다 - 사용자 활동 시간과 피크 시간이 겹치는 구간이라 중요하다.
    """
    u = datetime.fromtimestamp(t_ms / 1000, tz=timezone.utc)
    return u.weekday() < 5 and 12 <= u.hour < 18


def _aggregate(recs: List[Dict[str, Any]]) -> Dict[str, Any]:
    total = len(recs)

    # 요청 수: kind × outcome
    by_kind_outcome: Dict[str, Dict[str, int]] = {}
    for r in recs:
        k, o = r.get("kind", "?"), r.get("outcome", "?")
        by_kind_outcome.setdefault(k, {}).setdefault(o, 0)
        by_kind_outcome[k][o] += 1

    # pHash 스킵률 - outcome이 아니라 path로 센다 (chat·event는 이미지가 있어도
    # 화면과 무관하게 응답하므로 outcome은 ok인데 path는 phash_skip일 수 있다).
    with_path = [r for r in recs if r.get("path") != "none"]
    phash_skip = sum(1 for r in with_path if r.get("path") == "phash_skip")
    phash_rate = phash_skip / len(with_path) if with_path else None

    # 비전 생략률
    vision_or_ocr = [r for r in recs if r.get("path") in ("ocr_only", "vision")]
    ocr_only = sum(1 for r in vision_or_ocr if r.get("path") == "ocr_only")
    vision_skip_rate = ocr_only / len(vision_or_ocr) if vision_or_ocr else None

    # 캐시 적중률 - 대사/화면 요청만 본다. 회고·증류는 매번 새 재료(그날 대화·관찰)를
    # 앞에 붙여 보내는 프롬프트라 원래 캐시가 안 걸린다 - 섞으면 "프롬프트 배치
    # 덕분에 캐시가 걸린다"는 주장과 무관한 숫자가 분모에 끼어 비율이 내려간다.
    # (실측: 대사만 63.5% vs 섞으면 45.2% - 하루 치만 쌓였을 때 특히 크게 흔들린다)
    chat_recs = [r for r in recs if r.get("kind") not in ("episode", "distill")]
    distill_recs = [r for r in recs if r.get("kind") in ("episode", "distill")]

    with_usage = [r for r in chat_recs if r.get("cached_tokens") is not None and r.get("prompt_tokens")]
    cached_sum = sum(r["cached_tokens"] for r in with_usage)
    prompt_sum = sum(r["prompt_tokens"] for r in with_usage)
    cache_rate = cached_sum / prompt_sum if prompt_sum else None

    distill_with_usage = [r for r in distill_recs if r.get("cached_tokens") is not None and r.get("prompt_tokens")]
    distill_cached_sum = sum(r["cached_tokens"] for r in distill_with_usage)
    distill_prompt_sum = sum(r["prompt_tokens"] for r in distill_with_usage)
    distill_cache_rate = distill_cached_sum / distill_prompt_sum if distill_prompt_sum else None

    # 지연 - path별 p50/p95. 같은 이유로 회고·증류를 뺀다 (path는 항상 "none"이라
    # 안 빼면 말 걸기·이벤트의 "none" 지연과 섞인다).
    latency: Dict[str, Dict[str, Any]] = {}
    for path in ("vision", "ocr_only", "none"):
        vals = [r["ms_total"] for r in chat_recs
                if r.get("path") == path and r.get("ms_total") is not None]
        latency[path] = {"n": len(vals), "p50": pct(vals, 50), "p95": pct(vals, 95)}

    # 차단
    blocked_count = sum(1 for r in recs if r.get("path") == "blocked")
    redacted_sum = sum(r.get("redacted") or 0 for r in recs)

    # 장애
    error_counts: Dict[str, int] = {}
    for r in recs:
        if r.get("outcome") == "cloud_error":
            e = r.get("error") or "?"
            error_counts[e] = error_counts.get(e, 0) + 1

    # 토큰 합계 - 피크/비피크 × 대사/회고·증류로 나눈다. 회고·증류도 그날 첫 요청
    # 때 돌고(ensure_recent/ensure), 저녁에 주로 켜면 거의 매일 피크에 걸린다 -
    # 항상 비피크로 계산하면 안 된다 (실측: 증류가 21:51에 돌아 피크였음).
    tokens = {
        "chat_peak": {"prompt": 0, "cached": 0, "output": 0},
        "chat_off": {"prompt": 0, "cached": 0, "output": 0},
        "episode_distill_peak": {"prompt": 0, "cached": 0, "output": 0},
        "episode_distill_off": {"prompt": 0, "cached": 0, "output": 0},
    }
    for r in recs:
        p, c, o = r.get("prompt_tokens"), r.get("cached_tokens"), r.get("output_tokens")
        if p is None and c is None and o is None:
            continue
        peak = is_peak(r.get("t", 0))
        if r.get("kind") in ("episode", "distill"):
            bucket = tokens["episode_distill_peak"] if peak else tokens["episode_distill_off"]
        else:
            bucket = tokens["chat_peak"] if peak else tokens["chat_off"]
        bucket["prompt"] += p or 0
        bucket["cached"] += c or 0
        bucket["output"] += o or 0

    # 예상 요금 - 단가가 전부 설정돼 있을 때만 계산한다 (추측한 단가로 계산하지 않는다)
    cost_usd = None
    if PRICE_INPUT is not None and PRICE_CACHED_INPUT is not None and PRICE_OUTPUT is not None:
        def _bucket_cost(bucket: Dict[str, int], peak: bool) -> float:
            mult = PRICE_PEAK_MULTIPLIER if (peak and PRICE_PEAK_MULTIPLIER) else 1.0
            uncached = max(0, bucket["prompt"] - bucket["cached"])
            input_cost = (uncached * PRICE_INPUT + bucket["cached"] * PRICE_CACHED_INPUT) / 1_000_000
            output_cost = bucket["output"] * PRICE_OUTPUT / 1_000_000
            return (input_cost + output_cost) * mult

        cost_usd = (
            _bucket_cost(tokens["chat_peak"], True)
            + _bucket_cost(tokens["chat_off"], False)
            + _bucket_cost(tokens["episode_distill_peak"], True)
            + _bucket_cost(tokens["episode_distill_off"], False)
        )

    return {
        "total": total,
        "by_kind_outcome": by_kind_outcome,
        "phash_skip_rate": phash_rate, "phash_skip_n": len(with_path),
        "vision_skip_rate": vision_skip_rate, "vision_skip_n": len(vision_or_ocr),
        "cache_rate": cache_rate, "cache_n": len(with_usage), "cache_total_n": len(chat_recs),
        "distill_cache_rate": distill_cache_rate, "distill_cache_n": len(distill_with_usage),
        "distill_cache_total_n": len(distill_recs),
        "latency": latency,
        "blocked_count": blocked_count, "redacted_sum": redacted_sum,
        "error_counts": error_counts,
        "tokens": tokens,
        "cost_usd": cost_usd,
        "price_checked": PRICE_CHECKED,
    }


# ───────────────── 라우터 ─────────────────

@router.get("/api")
async def stats_api(days: int = 7):
    recs = _read(days)
    return JSONResponse({"days": days, **_aggregate(recs)})


def _pct_str(x: Optional[float]) -> str:
    return "모름" if x is None else f"{x * 100:.1f}%"


def _ms_str(x: Optional[float]) -> str:
    return "–" if x is None else f"{int(x)}ms"


def _kind_outcome_table(d: Dict[str, Dict[str, int]]) -> str:
    if not d:
        return "<p class='empty'>아직 기록이 없다.</p>"
    kinds = sorted(d.keys())
    outcomes = sorted({o for v in d.values() for o in v})
    head = "".join(f"<th>{o}</th>" for o in outcomes)
    rows = "".join(
        f"<tr><td>{k}</td>" + "".join(f"<td>{d[k].get(o, 0)}</td>" for o in outcomes) + "</tr>"
        for k in kinds
    )
    return f"<table><tr><th>kind</th>{head}</tr>{rows}</table>"


def _render_html(days: int, a: Dict[str, Any]) -> str:
    kind_table = _kind_outcome_table(a["by_kind_outcome"])

    lat_rows = "".join(
        f"<tr><td>{path}</td><td>{v['n']}</td><td>{_ms_str(v['p50'])}</td><td>{_ms_str(v['p95'])}</td></tr>"
        for path, v in a["latency"].items()
    )

    err_rows = "".join(
        f"<tr><td>{k}</td><td>{v}</td></tr>" for k, v in sorted(a["error_counts"].items())
    ) or "<tr><td colspan='2'>없음</td></tr>"

    tok = a["tokens"]
    tok_rows = "".join(
        f"<tr><td>{label}</td><td>{b['prompt']}</td><td>{b['cached']}</td><td>{b['output']}</td></tr>"
        for label, b in (
            ("대화·화면 (피크)", tok["chat_peak"]),
            ("대화·화면 (비피크)", tok["chat_off"]),
            ("회고·증류 (피크)", tok["episode_distill_peak"]),
            ("회고·증류 (비피크)", tok["episode_distill_off"]),
        )
    )

    if a["cost_usd"] is not None:
        cost_html = f"<p>예상 요금 ({a['price_checked']} 단가 기준): <b>${a['cost_usd']:.4f}</b></p>"
    else:
        cost_html = "<p class='note'>예상 요금: 단가가 아직 설정되지 않음 (config.py의 PRICE_* 참고)</p>"

    return f"""<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>미진이 계측</title>
<style>
  :root{{--night:#1A1628;--panel:#241E38;--rule:#3A3158;--text:#E4E0F0;--dim:#8279A4;--mijin:#38E8D0}}
  body{{background:var(--night);color:var(--text);font-family:Pretendard,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;
        padding:28px;line-height:1.6;max-width:880px}}
  h1{{font-size:19px;margin-bottom:4px}}
  h2{{font-size:14px;color:var(--dim);margin-top:30px;margin-bottom:6px}}
  table{{border-collapse:collapse;margin-top:4px}}
  td,th{{border:1px solid var(--rule);padding:4px 11px;font-size:13px;text-align:right}}
  th:first-child,td:first-child{{text-align:left}}
  .note{{color:var(--dim);font-size:12.5px}}
  .empty{{color:var(--dim)}}
  a{{color:var(--mijin);text-decoration:none;margin-right:4px}}
  a:hover{{text-decoration:underline}}
</style>
</head>
<body>
<h1>미진이 계측 — 최근 {days}일 ({a['total']}건)</h1>
<p class="note">
  <a href="?days=1">1일</a>· <a href="?days=3">3일</a>· <a href="?days=7">7일</a>·
  <a href="?days=30">30일</a>· <a href="/stats/api?days={days}">JSON</a>
</p>

<h2>요청 수 (kind × outcome)</h2>
{kind_table}

<h2>스킵 · 생략</h2>
<p>pHash 스킵률: {_pct_str(a['phash_skip_rate'])} (보낸 캡처 {a['phash_skip_n']}건 중)</p>
<p>비전 생략률: {_pct_str(a['vision_skip_rate'])} (화면 분석 {a['vision_skip_n']}건 중)</p>

<h2>캐시 적중률</h2>
<p>대사·화면: {_pct_str(a['cache_rate'])} ({a['cache_n']}건 / 전체 {a['cache_total_n']}건에 usage 있음)</p>
<p class="note">회고·증류: {_pct_str(a['distill_cache_rate'])} ({a['distill_cache_n']}건 / 전체 {a['distill_cache_total_n']}건에 usage 있음) —
매번 그날 재료를 새로 앞에 붙이는 프롬프트라 원래 캐시가 안 걸린다. 위 수치와 따로 본다.</p>

<h2>지연 (ms_total, path별)</h2>
<table><tr><th>path</th><th>건수</th><th>p50</th><th>p95</th></tr>{lat_rows}</table>

<h2>차단</h2>
<p>차단된 요청: {a['blocked_count']}건 · 비밀정보 제거: {a['redacted_sum']}건</p>

<h2>장애</h2>
<table><tr><th>error</th><th>건수</th></tr>{err_rows}</table>

<h2>토큰 합계</h2>
<table><tr><th>구분</th><th>prompt</th><th>cached</th><th>output</th></tr>{tok_rows}</table>
{cost_html}

<p class="note" style="margin-top:28px">클라이언트가 자리 비움·가리기·들림·잠·전체화면일 때는 요청 자체를
보내지 않아서 여기 안 잡힌다. 그래서 위 비율들은 "전체 시간 중"이 아니라
"보낸 캡처 중" 기준이다.</p>
</body>
</html>"""


@router.get("", response_class=HTMLResponse)
@router.get("/", response_class=HTMLResponse)
async def stats_page(days: int = 7):
    recs = _read(days)
    return HTMLResponse(_render_html(days, _aggregate(recs)))
