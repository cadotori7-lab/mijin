"""미진이 대화 기록 뷰어.

프록시(proxy_server.py)에 라우터로 붙여서 localhost:8000/viewer 로 연다.
memory.py가 쌓는 jsonl을 읽기 전용으로만 열며, 아무것도 쓰지 않는다.
 
proxy_server.py에 두 줄만 추가하면 된다:
 
    from viewer import router as viewer_router
    app.include_router(viewer_router)
"""
 
import json
from datetime import datetime, date as _date
from pathlib import Path
from typing import Any, Dict, List
 
from fastapi import APIRouter
from fastapi.responses import HTMLResponse, JSONResponse
 
# ================= 설정 =================
# 미진이 기억은 프록시가 직접 쌓는다. memory.py와 같은 파일을 읽기 전용으로 연다.
from config import MEM_DIR, OBSERVATION_LOG as _OBS

MEM = Path(MEM_DIR)
CHAT_FILES = [MEM / "chat_archive.jsonl", MEM / "chat_history.jsonl", MEM / "events.jsonl"]
MONO_FILE = MEM / "monologue.jsonl"
EPISODE_FILE = MEM / "episodes.jsonl"
OBSERVATION_LOG = Path(_OBS)

# events.jsonl은 role이 "event"/"mijin"으로 대화와 같은 2레코드 형태라 CHAT_FILES에
# 같이 넣어 시간순으로 섞는다. "event"만 "상황"으로 구분 표시해, 찌르기 같은
# 지문이 사람이 친 말처럼 보이지 않게 한다.
SPEAKER_NAMES = {"user": "민재", "mijin": "미진", "self": "미진(혼잣말)", "event": "상황"}
# =======================================

router = APIRouter(prefix="/viewer")


def _read_jsonl(path: Path) -> List[Dict[str, Any]]:
    """읽기 전용. 프록시가 쓰는 중이라 마지막 줄이 깨져 있을 수 있으므로 건너뛴다."""
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
                    continue  # 쓰는 중인 줄
    except Exception as e:
        print(f"[VIEWER] {path.name} 읽기 실패: {e}")
    return out


def _load_chat() -> List[Dict[str, Any]]:
    """대화 전체. 아카이브가 먼저, 최근 기록이 나중이다."""
    recs: List[Dict[str, Any]] = []
    for p in CHAT_FILES:
        recs += _read_jsonl(p)
    recs.sort(key=lambda r: r.get("t", 0))
    return recs


def _load_monologue() -> List[Dict[str, Any]]:
    return _read_jsonl(MONO_FILE)


def _load_episodes() -> List[Dict[str, Any]]:
    return _read_jsonl(EPISODE_FILE)


def _day_of(ms: int) -> str:
    return datetime.fromtimestamp(ms / 1000).strftime("%Y-%m-%d")


def _hhmm(ms: int) -> str:
    return datetime.fromtimestamp(ms / 1000).strftime("%H:%M")


def _diagnose() -> None:
    if not MEM.exists():
        print(f"[VIEWER] 기억 폴더가 아직 없습니다: {MEM}")
        print("[VIEWER] 미진이와 대화를 나누면 자동으로 생깁니다.")
        return
    print(f"[VIEWER] 대화 {len(_load_chat())}줄 / 혼잣말 {len(_load_monologue())}건 "
          f"/ 회고 {len(_load_episodes())}일치")


_diagnose()
 
 
def _episode_day(raw: str) -> str:
    """episodes.jsonl의 date는 가끔 '2026-09-08~2026-09-09' 같은 범위로 들어온다.
    (자정을 넘겨 이어진 세션) 이 경우 끝 날짜에 붙여서 레일에 빈 항목이 생기지 않게 한다."""
    if not raw:
        return ""
    return raw.split("~")[-1].strip()
 
 
@router.get("/api/days")
async def api_days():
    """날짜 목록 + 날짜별 건수. 좌측 레일을 그리는 데 쓴다."""
    chat = _load_chat()
    obs = _read_jsonl(OBSERVATION_LOG)
    eps = _load_episodes()
 
    days: Dict[str, Dict[str, int]] = {}
 
    def bump(day: str, key: str):
        days.setdefault(day, {"talk": 0, "watch": 0, "recap": 0})[key] += 1
 
    for r in chat:
        if r.get("t"):
            bump(_day_of(r["t"]), "talk")
    for r in _load_monologue():
        if r.get("t"):
            bump(_day_of(r["t"]), "watch")
    for r in obs:
        if r.get("t"):
            bump(_day_of(r["t"]), "watch")
    for r in eps:
        d = _episode_day(r.get("date", ""))
        if d:
            bump(d, "recap")
 
    today = _date.today().isoformat()
    out = [{"date": d, **c, "isToday": d == today} for d, c in sorted(days.items(), reverse=True)]
    return JSONResponse(out)
 
 
SESSION_GAP_MS = 10 * 60 * 1000   # 10분 이상 끊기면 다른 대화 묶음으로 본다
 
 
@router.get("/api/talk")
async def api_talk(date: str = ""):
    """대화 탭 — user/mijin만. self(혼잣말)는 관찰 탭으로 간다.

    새로고침하며 보는 용도라 최신이 위로 와야 하지만, 메시지를 그냥 뒤집으면
    답이 질문보다 위에 와서 거꾸로 읽힌다. 그래서 10분 단위 묶음으로 나눈 뒤
    묶음의 순서만 뒤집고, 묶음 안에서는 질문 -> 답 순서를 그대로 둔다."""
    msgs = []
    for r in _load_chat():
        if not r.get("t"):
            continue
        if date and _day_of(r["t"]) != date:
            continue
        msgs.append(r)
 
    sessions: List[Dict[str, Any]] = []
    for r in msgs:
        if not sessions or r["t"] - sessions[-1]["endT"] > SESSION_GAP_MS:
            sessions.append({"endT": r["t"], "start": _hhmm(r["t"]), "turns": []})
        sessions[-1]["endT"] = r["t"]
        sessions[-1]["turns"].append({
            "time": _hhmm(r["t"]),
            "role": r["role"],
            "who": SPEAKER_NAMES.get(r["role"], r["role"]),
            "text": r.get("text", ""),
        })
 
    for s in sessions:
        s["end"] = _hhmm(s["endT"])
        del s["endT"]
    sessions.reverse()
    return JSONResponse(sessions)
 
 
@router.get("/api/watch")
async def api_watch(date: str = ""):
    """관찰 탭 — 미진이의 혼잣말 + 화면 관찰 로그를 시간순으로 합친다."""
    rows = []
    for r in _load_monologue():
        if not r.get("t"):
            continue
        if date and _day_of(r["t"]) != date:
            continue
        rows.append({"t": r["t"], "time": _hhmm(r["t"]), "kind": "said",
                     "text": r.get("text", ""), "win": "", "ocr": ""})
 
    for r in _read_jsonl(OBSERVATION_LOG):
        if not r.get("t"):
            continue
        if date and _day_of(r["t"]) != date:
            continue
        rows.append({"t": r["t"], "time": _hhmm(r["t"]), "kind": "seen",
                     "text": r.get("scene", ""), "win": r.get("win", ""),
                     "ocr": r.get("ocr", "")})
 
    rows.sort(key=lambda x: x["t"], reverse=True)
    return JSONResponse(rows)
 
 
@router.get("/api/recap")
async def api_recap(date: str = ""):
    rows = [r for r in _load_episodes()
            if not date or _episode_day(r.get("date", "")) == date]
    rows.reverse()
    return JSONResponse(rows)
 
 
@router.get("/api/greeting")
async def api_greeting():
    """가장 최근 미진이 발화. 뷰어를 열면 미진이가 맞이하는 자리."""
    for r in reversed(_load_chat()):
        if r.get("role") == "mijin" and (r.get("text") or "").strip():
            return JSONResponse({"text": r["text"].strip(), "time": _hhmm(r["t"])})
    return JSONResponse({"text": "", "time": ""})
 
 
@router.get("", response_class=HTMLResponse)
@router.get("/", response_class=HTMLResponse)
async def viewer_page():
    return HTMLResponse(PAGE)
 
 
PAGE = r"""<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>미진이와 지낸 날</title>
<link rel="stylesheet" as="style" crossorigin
 href="https://cdn.jsdelivr.net/gh/orioncactus/pretendard@v1.3.9/dist/web/static/pretendard.min.css">
<style>
:root{
  --night:#1A1628; --panel:#241E38; --rise:#2C2544; --rule:#3A3158;
  --text:#E4E0F0; --dim:#8279A4; --faint:#5F5680;
  --mijin:#38E8D0;  /* 미진 */
  --bori:#D69A6A;  /* 민재 */
  --pad:clamp(16px,3vw,32px);
}
*{box-sizing:border-box}
html,body{margin:0;height:100%}
body{
  background:var(--night); color:var(--text);
  font-family:Pretendard,-apple-system,"Segoe UI","Malgun Gothic",sans-serif;
  font-size:15px; line-height:1.65;
  font-feature-settings:"tnum";
}
button{font:inherit;color:inherit;background:none;border:0;cursor:pointer}
:focus-visible{outline:2px solid var(--mijin);outline-offset:2px;border-radius:3px}
 
/* ── 상단: 미진이의 마지막 말 ── */
header{padding:var(--pad) var(--pad) 0}
.greet{
  max-width:62ch; border-left:2px solid var(--mijin);
  padding:2px 0 2px 16px; margin:0 0 4px;
  font-size:clamp(17px,2.4vw,21px); line-height:1.5;
  color:var(--text); white-space:pre-wrap;
}
.greet-meta{color:var(--faint);font-size:12.5px;padding-left:18px}
.greet-meta b{color:var(--mijin);font-weight:500}
 
/* ── 탭 ── */
nav{display:flex;gap:2px;padding:20px var(--pad) 0;border-bottom:1px solid var(--rule)}
nav button{padding:9px 15px;color:var(--dim);border-bottom:2px solid transparent;margin-bottom:-1px}
nav button:hover{color:var(--text)}
nav button[aria-selected="true"]{color:var(--text);border-bottom-color:var(--mijin)}
nav .n{color:var(--faint);font-size:12px;margin-left:6px}
#live{margin-left:auto;align-self:center;color:var(--faint);font-size:12px;padding-right:2px}
 
/* ── 본문 2열 ── */
main{display:grid;grid-template-columns:168px minmax(0,1fr);height:calc(100% - 0px)}
#rail{border-right:1px solid var(--rule);overflow-y:auto;padding:14px 0 40px}
#rail h2{font-size:11.5px;color:var(--faint);font-weight:500;margin:0 0 8px;padding:0 var(--pad)}
.day{display:block;width:100%;text-align:left;padding:7px var(--pad);color:var(--dim);position:relative}
.day:hover{background:var(--panel);color:var(--text)}
.day[aria-current="true"]{background:var(--rise);color:var(--text)}
.day[aria-current="true"]::before{content:"";position:absolute;left:0;top:0;bottom:0;width:2px;background:var(--mijin)}
.day .d{font-size:13.5px}
.day .bars{display:flex;gap:3px;margin-top:4px;height:3px}
.day .bar{height:3px;border-radius:2px}
.bar.talk{background:var(--bori)}
.bar.watch{background:var(--faint)}
 
#pane{overflow-y:auto;padding:calc(var(--pad) - 4px) var(--pad) 80px}
 
/* ── 대화: 말풍선 대신 대본 형식 ── */
.sess{padding:0 0 22px}
.sess + .sess{border-top:1px solid var(--panel);padding-top:18px}
.sess-head{color:var(--faint);font-size:12.5px;margin-bottom:2px;padding-left:102px}
.turn{display:grid;grid-template-columns:88px minmax(0,1fr);gap:14px;padding:9px 0}
.turn .meta{text-align:right;color:var(--faint);font-size:12.5px;padding-top:2px}
.turn .meta .who{display:block;font-size:13px}
.turn .body{max-width:64ch;white-space:pre-wrap;padding-left:14px;border-left:2px solid var(--rule)}
.turn.mijin .meta .who{color:var(--mijin)}
.turn.mijin .body{border-left-color:var(--mijin)}
.turn.user .meta .who{color:var(--bori)}
.turn.user .body{border-left-color:var(--bori)}
 
/* ── 관찰 ── */
.obs{display:grid;grid-template-columns:56px minmax(0,1fr);gap:14px;padding:7px 0;border-bottom:1px solid var(--panel)}
.obs .time{color:var(--faint);font-size:12.5px;padding-top:2px}
.obs .win{color:var(--dim);font-size:12.5px;margin-bottom:2px}
.obs .scene{max-width:70ch;color:var(--text)}
.obs.said .scene{color:var(--mijin)}
.obs details{margin-top:5px}
.obs summary{color:var(--faint);font-size:12.5px;width:fit-content}
.obs summary:hover{color:var(--dim)}
.obs .ocr{margin-top:6px;padding:9px 11px;background:var(--panel);border-radius:5px;
  color:var(--dim);font-size:13px;white-space:pre-wrap;max-width:74ch}
 
/* ── 회고 ── */
.recap{max-width:66ch;padding:16px 0;border-bottom:1px solid var(--panel)}
.recap .d{color:var(--mijin);font-size:13px;margin-bottom:7px}
.recap .kw{display:flex;flex-wrap:wrap;gap:6px;margin-top:11px}
.recap .kw span{background:var(--panel);color:var(--dim);font-size:12.5px;padding:2px 9px;border-radius:11px}
 
.empty{color:var(--faint);padding:40px 0;max-width:52ch}
@media (max-width:720px){
  main{grid-template-columns:1fr;height:auto}
  #rail{border-right:0;border-bottom:1px solid var(--rule);max-height:132px}
}
@media (prefers-reduced-motion:no-preference){
  .greet{animation:in .5s ease-out}
  @keyframes in{from{opacity:0;transform:translateY(4px)}to{opacity:1}}
}
</style>
</head>
<body>
<header>
  <p class="greet" id="greet">기록을 불러오는 중</p>
  <p class="greet-meta" id="greetMeta"></p>
</header>
 
<nav role="tablist">
  <button role="tab" data-tab="talk" aria-selected="true">대화<span class="n" id="nTalk"></span></button>
  <button role="tab" data-tab="watch" aria-selected="false">관찰<span class="n" id="nWatch"></span></button>
  <button role="tab" data-tab="recap" aria-selected="false">회고<span class="n" id="nRecap"></span></button>
  <span id="live"></span>
</nav>
 
<main>
  <div id="rail"><h2>날짜</h2><div id="days"></div></div>
  <div id="pane"><p class="empty">불러오는 중</p></div>
</main>
 
<script>
const $ = s => document.querySelector(s);
const esc = s => (s||"").replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
let tab = "talk", day = "", days = [], lastJSON = "";
 
async function get(u){ const r = await fetch(u); return r.ok ? r.json() : []; }
 
function drawRail(){
  // 관찰(수백 건)과 대화(수십 건)는 자릿수가 달라서 같은 기준으로 재면
  // 대화 막대가 보이지 않는다. 종류별로 따로 정규화한다.
  const maxTalk  = Math.max(1, ...days.map(d => d.talk));
  const maxWatch = Math.max(1, ...days.map(d => d.watch));
  const W = 46;  // 막대 하나의 최대 폭 (레일 안에 두 개가 나란히 들어가는 크기)
  const bar = (n, max) => n ? Math.max(3, Math.round(n / max * W)) : 0;
 
  $("#days").innerHTML = days.map(d => `
    <button class="day" data-d="${d.date}" aria-current="${d.date===day}"
      title="대화 ${d.talk} · 관찰 ${d.watch} · 회고 ${d.recap}">
      <span class="d">${d.date.slice(5).replace('-','월 ')}일${d.isToday?' · 오늘':''}</span>
      <span class="bars">
        <span class="bar talk" style="width:${bar(d.talk, maxTalk)}px"></span>
        <span class="bar watch" style="width:${bar(d.watch, maxWatch)}px"></span>
      </span></button>`).join("");
 
  const sum = k => days.reduce((a,d) => a + d[k], 0);
  $("#nTalk").textContent = sum("talk");
  $("#nWatch").textContent = sum("watch");
  $("#nRecap").textContent = sum("recap");
}
 
const views = {
  talk: ss => ss.length ? ss.map(s => `
    <section class="sess">
      <div class="sess-head">${s.start}${s.end!==s.start?` – ${s.end}`:''}</div>
      ${s.turns.map(r => `
        <div class="turn ${r.role}">
          <div class="meta"><span class="who">${esc(r.who)}</span>${r.time}</div>
          <div class="body">${esc(r.text)}</div>
        </div>`).join("")}
    </section>`).join("")
    : `<p class="empty">이 날은 미진이와 나눈 대화가 없어요. 관찰 탭에는 기록이 있을 수 있습니다.</p>`,
 
  watch: rows => rows.length ? rows.map(r => `
    <div class="obs ${r.kind}">
      <div class="time">${r.time}</div>
      <div>
        ${r.win ? `<div class="win">${esc(r.win)}</div>` : ""}
        <div class="scene">${esc(r.text)}</div>
        ${r.ocr ? `<details><summary>화면에서 읽은 글자</summary>
                   <div class="ocr">${esc(r.ocr)}</div></details>` : ""}
      </div>
    </div>`).join("")
    : `<p class="empty">이 날은 관찰 기록이 없어요.</p>`,
 
  recap: rows => rows.length ? rows.map(r => `
    <div class="recap">
      <div class="d">${esc(r.date)}</div>
      <div>${esc(r.summary)}</div>
      <div class="kw">${(r.keywords||[]).map(k=>`<span>${esc(k)}</span>`).join("")}</div>
    </div>`).join("")
    : `<p class="empty">이 날의 회고는 아직 없어요.</p>`
};
 
async function render(){
  $("#pane").innerHTML = `<p class="empty">불러오는 중</p>`;
  const rows = await get(`/viewer/api/${tab}?date=${encodeURIComponent(day)}`);
  lastJSON = JSON.stringify(rows);
  $("#pane").innerHTML = views[tab](rows);
  $("#pane").scrollTop = 0;
}
 
// 오늘을 보고 있을 때만 조용히 갱신한다. 내용이 그대로면 화면을 건드리지 않아
// 읽는 중에 스크롤이 튀지 않는다.
async function poll(){
  const d = days.find(x => x.date === day);
  if (!d || !d.isToday || document.hidden) return;
  const rows = await get(`/viewer/api/${tab}?date=${encodeURIComponent(day)}`);
  const now = JSON.stringify(rows);
  if (now === lastJSON) return;
  lastJSON = now;
  const keep = $("#pane").scrollTop;
  $("#pane").innerHTML = views[tab](rows);
  $("#pane").scrollTop = keep;   // 최신이 위에 붙으므로 읽던 위치가 유지된다
  $("#live").textContent = "새 기록 " + new Date().toLocaleTimeString('ko-KR',{hour:'2-digit',minute:'2-digit'});
  days = await get('/viewer/api/days');
  drawRail();
}
setInterval(poll, 30000);
 
document.querySelectorAll('nav button').forEach(b => b.onclick = () => {
  document.querySelectorAll('nav button').forEach(x => x.setAttribute('aria-selected', x===b));
  tab = b.dataset.tab; render();
});
 
$("#days").onclick = e => {
  const b = e.target.closest('.day'); if(!b) return;
  day = b.dataset.d; drawRail(); render();
};
 
(async () => {
  const g = await get('/viewer/api/greeting');
  if (g.text) {
    $("#greet").textContent = g.text;
    $("#greetMeta").innerHTML = `<b>미진이</b>가 마지막으로 한 말 · ${g.time}`;
  } else {
    $("#greet").textContent = "아직 기록이 없어요.";
    $("#greetMeta").textContent = "미진이와 대화를 나누면 여기에 쌓입니다.";
  }
  days = await get('/viewer/api/days');
  if (days.length) day = days[0].date;
  drawRail();
  render();
})();
</script>
</body>
</html>
"""
 