# 미진이 (Mijin) — 작업 지침

바탕화면 마스코트. 화면을 캡처해 읽고, 캐릭터가 한 마디 하고, 음성으로 말한다.

```
Unity 본체 ──HTTP──▶ FastAPI 프록시 ──▶ 로컬 Ollama (OCR · 비전)
  (C#)                 (Python)      ──▶ 클라우드 LLM (대사)
```

**본체는 얇고, 판단은 전부 프록시에 있다.** 무엇을 읽을지·버릴지·보낼지·기억할지는
모두 `server/`에서 정한다. 기능을 추가할 때 이 경계를 먼저 확인할 것. 클라이언트에
판단 로직을 넣으면 모델이나 규칙을 바꿀 때마다 Unity를 다시 빌드해야 한다.

## 폴더

```
server/       FastAPI 프록시 (여기가 본체다)
  config.py     모든 설정값. 숫자·목록·프롬프트 문구만 둔다
  vision.py     캡처 분석 (pHash → 차단 검사 → OCR → 비전)
  context.py    프로필·화면 기억 주입, 메시지 조립
  mijin.py      /mijin/* 엔드포인트
  memory.py     jsonl 기억 저장소
  episode.py    하루치 회고
  distill.py    관찰 로그 → 사용자 추정 프로필
  viewer.py     /viewer 웹 뷰어
client/       Unity 프로젝트
sprite/       메디방 원본 + mdp_split.py
```

## 명령

```bash
cd server
.venv\Scripts\activate
python proxy_server.py          # http://127.0.0.1:8000/docs, /viewer
```

자동 테스트는 없다. 확인은 서버 시작 로그의 `[OPTION]` 줄과 요청 시 `[INFO]` 줄로 한다.
변경 후에는 실제로 한 번 띄워서 로그를 읽을 것.

---

# 깨뜨리면 안 되는 것

아래는 전부 **실제로 한 번씩 터졌던 것**이다. 한 파일만 보고 고치면 조용히 되돌아간다.
고쳐야 할 이유가 생기면 되돌리기 전에 사용자에게 먼저 물을 것.

### 1. 차단된 창은 OCR과 비전 호출을 **둘 다** 막는다

`vision.py`의 `_is_blocked_window()` 분기는 OCR보다 **먼저** 오고 거기서 끝나야 한다.

OCR만 끄면 `ocr_text`가 비고 → `_needs_vision()`이 True가 되고 → OCR이 없으니
"화면 텍스트를 그대로 나열하라"는 `VISION_PROMPT`가 선택된다. 결과적으로 비전 모델이
비밀번호 관리자 화면을 **대신 옮겨 적어** 클라우드로 보낸다. 창 제목 자체도 노출
정보다 (카카오톡 제목은 대화 상대 이름이다).

### 2. 프롬프트 앞부분 순서를 바꾸지 않는다

```
system   페르소나 + 회고        거의 안 바뀜 → 캐시됨
history  최근 N턴               가끔 바뀜
last user 화면 기억 + 리마인더   매번 바뀜
```

화면 기억을 시스템 메시지로 옮기면 **매 요청 캐시가 통째로 깨진다.** 요금이 이 한 줄에
달려 있다. `context.py`의 `_inject_profile()`은 system에, `clean_messages()` 5단계는
마지막 user 메시지에 넣는다 — 이 배치가 의도된 것이다.

캐시가 실제로 걸리는지는 응답의 `usage.prompt_tokens_details.cached_tokens`로 확인한다.
프롬프트 구조를 건드렸으면 이 값을 찍어보고 보고할 것.

### 3. 창 제목은 본체가 캡처와 같은 시점에 읽어 보낸다

`extract_and_analyze_image(messages, window_title=...)`의 인자를 우선한다.
프록시가 나중에 `_active_window_title()`로 다시 읽으면, 그 사이 창이 바뀌어
**차단해야 할 화면이 엉뚱한 이름표를 달고 통과한다.**

과거에 인자를 받아놓고 아래에서 다시 덮어쓰는 줄이 남아 있어 창 제목이 항상 "Mijin"으로
찍히는 버그가 있었다. 인자가 있으면 그대로 쓸 것.

### 4. `profile.txt`와 `distilled_profile.txt`는 다른 라벨로 주입한다

`config.py`의 `PROFILE_FILES`에 파일별 라벨이 붙어 있다. 증류 프로필은 **화면에서 읽은
텍스트에서 뽑은 추정치**라, 손으로 쓴 확정 사실과 같은 신뢰도로 주입하면 화면에 우연히
뜬 문장이나 프롬프트 주입 시도가 "사용자에 대한 사실"로 박힌다. 라벨을 합치거나 떼지 말 것.

### 5. pHash 커밋은 분석이 끝난 뒤에 한다

`_compute_phash` → `_screen_changed` → (분석) → `_commit_phash` 순서다.
앞에서 커밋하면 도중에 실패한 프레임이 "이미 본 화면"으로 남아 영영 재시도되지 않는다.
차단 창은 예외로 즉시 커밋한다(재분석할 내용이 없으므로).

### 6. 대화와 혼잣말은 다른 파일에 쌓는다

혼잣말이 훨씬 잦아서, 한 목록에 섞으면 반나절이면 진짜 대화가 밀려난다.
`memory.add_chat()` / `memory.add_monologue()`를 합치지 말 것.

### 7. 비밀은 커밋하지 않는다

`.env`, `fish_key.txt`, `mijin_settings.json`, `mem/`, `observations.jsonl`,
`profile.txt`, `distilled_profile.txt`. 키를 소스에 평문으로 두면 **이 프로그램 자신의
OCR이 그 파일을 열어둔 화면에서 읽어 클라우드로 보낸다.** 같은 이유로 `.env` 편집기
창 제목이 `OCR_BLOCKLIST`에 들어 있다.

### 8. 모델 태그를 임의로 바꾸지 않는다

`VISION_MODEL = "qwen3-vl:8b-instruct"`. `:8b` 태그는 실제로는 `-thinking` 모델이고,
`think:False`를 줘도 추론을 계속 뱉어 `num_predict`를 태우고 본문 없이 잘린다.
같은 화면에서 15~36초 → 1.8~3.2초 차이가 실측으로 확인됐다.

---

# 코드 규약

- **설정값은 전부 `config.py`로.** 로직 파일에 숫자를 박지 않는다.
- **주석은 "왜"를 쓴다.** 무엇을 하는지는 코드가 말한다. 기존 주석들이 이 스타일이고,
  실측값(몇 초 → 몇 초, 몇 자)이 들어 있으면 지우지 말 것.
- 주석·로그·문서는 한국어. 식별자는 영어.
- OCR·다운스케일 같은 동기 CPU 작업은 `asyncio.to_thread()`로 넘긴다. 그대로 두면
  이벤트 루프가 막혀 요청이 줄줄이 밀린다.
- 응답 `status`는 `"ok"` / `"skipped"` 두 가지다. `"skipped"`는 정상 동작(할 말 없음)이지
 오류가 아니다. 클라이언트에서 통신 실패와 구분해 다뤄야 한다.

# Unity 쪽 주의

- 모든 스프라이트는 **Sprite Mode: Single**, **Pivot: Custom (0.5, 0.253)**.
  Multiple로 들어오면 Unity가 투명 영역에서 자동 분할하고 피벗이 Center로 리셋되어
  얼굴이 머리 위에 뜬다.
- 자세별 발 높이 차이는 세 렌더러(body/eyes/mouth)를 **함께** 옮겨 보정한다.
  `localPosition`을 새로 만들지 말 것 — Z가 날아가 그리기 순서가 깨진다.
- 빌드 설정: Direct3D12 제거, "Use DXGI flip model swapchain for D3D11" 해제,
  Run In Background 켜기, Fullscreen Mode는 Windowed. DX12에서는 투명 창이 안 된다.
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`로 자기 창을 캡처에서 제외한다.
  이게 켜져 있으면 녹화 도구에도 안 찍히므로, 데모 영상을 찍을 땐 잠시 꺼야 한다.

# 작업 방식

- 고치기 전에 관련 파일을 먼저 읽는다. 위 항목들은 파일 하나만 봐서는 안 보인다.
- 위 "깨뜨리면 안 되는 것"에 닿는 변경은 **하기 전에** 이유를 말하고 확인을 받는다.
- 끝나면 바꾼 파일과 그 이유를 요약한다. 성능·요금·프라이버시에 영향이 있으면 명시한다.
- 사용자가 설계와 검증을 따로 맡고 있다. 스스로 판단해 구조를 바꾸기보다,
  애매하면 물어볼 것.
