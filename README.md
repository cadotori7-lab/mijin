# 미진이 (Mijin)

화면을 구경하고 말을 거는 데스크톱 마스코트.

바탕화면 위를 걸어다니는 캐릭터가 사용자의 화면을 보고 반응한다. 로컬에서 화면을 읽고, 필요한 만큼만 클라우드 모델에 넘겨 대사를 만들고, 음성으로 말한다.

<!-- TODO: 미진이가 바탕화면에서 말하는 GIF 또는 스크린샷 -->
<img width="560" height="519" alt="hold" src="https://github.com/user-attachments/assets/9ae34532-9eef-462b-b165-e270f743e81d" />
<img width="580" height="423" alt="poke" src="https://github.com/user-attachments/assets/962552b2-5453-48e7-86cd-8dfa2196e4bd" />
<img width="820" height="640" alt="screen" src="https://github.com/user-attachments/assets/bef005c5-1b1f-4f9d-bd87-a059af36866f" />

## 이런 걸 만들었다

- **화면을 본다** — 주기적으로 화면을 캡처해 OCR과 로컬 비전 모델로 읽는다
- **말을 건다** — 읽은 내용을 바탕으로 캐릭터가 한 마디 한다. 목소리가 나오고 입이 같이 움직인다
- **기억한다** — 대화와 관찰을 파일로 쌓고, 하루가 지나면 그날의 회고를 스스로 쓴다
- **가려진다** — 단축키 한 번으로 화면을 아예 안 보게 만들 수 있다
- **돌아본다** — 웹 뷰어에서 날짜별 대화·관찰·회고를 읽을 수 있다

## 구조

```
Unity 본체  ──HTTP──▶  프록시 서버  ──▶  로컬 모델 (OCR · 비전)
(C#)                   (Python)      ──▶  클라우드 모델 (대사)
    │                      │
    └── Fish Audio TTS     └── mem/*.jsonl
```

본체는 얇다. 캡처하고, 보내고, 받은 대사를 말풍선과 음성으로 내보낸다.

판단은 전부 프록시에 있다. 무엇을 읽을지, 무엇을 버릴지, 얼마나 보낼지, 무엇을 기억할지를 여기서 정한다. 모델을 바꾸거나 규칙을 고칠 때 본체를 다시 빌드하지 않아도 되고, 같은 프록시에 다른 클라이언트를 붙일 수도 있다.

### 폴더

```
server/                FastAPI 프록시
  proxy_server.py        라우터 조립과 시작 로그
  config.py              모든 설정값
  vision.py              캡처 분석 (OCR + 로컬 비전 모델)
  context.py             프로필·화면 기억 주입
  mijin.py               /mijin/chat 엔드포인트
  memory.py              jsonl 기억 저장소
  episode.py             하루치 회고 생성
  viewer.py              /viewer 웹 뷰어
  persona.txt            캐릭터 설정
client/                Unity 프로젝트
tools/mdp_split.py     메디방 .mdp를 레이어별 PNG로 분리
```

## 설계에서 신경 쓴 것

### 비용은 대부분 "보내지 않기"로 줄인다

대사 한 줄은 짧지만 그 뒤로 페르소나, 화면 기억, 대화 기록이 매번 따라간다. 마스코트는 하루에 수십 번 말하므로 여기가 요금의 거의 전부다.

- **화면이 그대로면 아예 안 보낸다.** 이전 프레임과 perceptual hash를 비교해 비슷하면 비전 모델도 클라우드도 부르지 않는다
- **같은 화면이 이어지면 간격을 늘린다.** 할 말이 없었던 횟수만큼 혼잣말 주기가 길어진다
- **창 종류에 따라 예산을 나눈다.** 코드나 원고 화면은 텍스트 전문(4000자)을 보내야 "변수명을 콕 집어 말하는" 반응이 나오지만, 유튜브 화면은 전문을 보내도 "재밌겠네요" 이상이 안 나온다. 그런 화면은 200자 요약만 보낸다
- **읽을 게 충분하면 비전 모델을 건너뛴다.** 문서 편집기에서 OCR이 충분히 나오면 장면 설명이 덧붙일 게 없다. 이 경로는 응답이 10배 빠르다

### 프롬프트 캐시가 유지되게 배치한다

앞부분이 매 요청 같아야 캐시 단가로 계산된다. 그래서 잘 안 바뀌는 것부터 순서대로 놓는다.

```
시스템 메시지  페르소나 + 회고        (거의 안 바뀜)
대화 기록      최근 N턴               (가끔 바뀜)
마지막 메시지  화면 기억 + 리마인더    (매번 바뀜)
```

화면 기억을 시스템 메시지가 아니라 마지막 사용자 메시지에 주입하는 이유가 이것이다. 회고를 하루 한 번만 갱신하는 것도 같은 이유다.

실제로 캐시가 걸리는지는 응답의 `usage.prompt_tokens_details.cached_tokens`를 로그로 찍어 확인한다.

### 화면에서 읽은 것은 밖으로 나가기 전에 걸러진다

화면 캡처는 사용자가 의도하지 않은 것까지 담는다. 그래서 여러 겹으로 막는다.

| 단계 | 하는 일 |
|---|---|
| 창 제목 차단 | 비밀번호 관리자, 메신저, `.env` 등은 OCR 자체를 하지 않는다 |
| 정규식 제거 | OCR 결과에서 API 키, 토큰, 이메일처럼 보이는 것을 지운다 |
| 자기 참조 차단 | 미진이 자신의 코드나 대사가 담긴 화면은 기록하지 않는다 |
| 가리기 | 사용자가 켜면 캡처 함수 자체가 동작하지 않는다 |

차단된 창에서 OCR만 끄면 안 된다. OCR 결과가 비면 "화면 텍스트를 그대로 나열하라"는 프롬프트가 선택되어, 비전 모델이 OCR 대신 비밀번호 관리자 화면을 옮겨 적는다. 그래서 차단 창은 비전 호출까지 통째로 막는다.

창 제목은 본체가 **캡처와 같은 시점에** 읽어 보낸다. 프록시가 나중에 따로 읽으면 그 사이에 창이 바뀌어, 차단해야 할 화면이 엉뚱한 이름표를 달고 통과한다.

### 기억은 종류별로 다르게 쌓는다

혼잣말은 사용자와의 대화보다 훨씬 잦다. 한 목록에 섞으면 혼잣말만으로 반나절이면 차서 진짜 대화가 밀려난다.

```
mem/chat_history.jsonl    최근 대화        프롬프트에 실림
mem/chat_archive.jsonl    밀려난 대화      뷰어와 검색용
mem/monologue.jsonl       혼잣말           최근 몇 건만 프롬프트에
mem/episodes.jsonl        날짜별 회고      최근 며칠치가 프롬프트에
observations.jsonl        화면 관찰 기록   회고의 원료
```

회고는 그날의 대화와 관찰을 모아 캐릭터 시점의 일기로 쓴다. 대화를 먼저 담고 남는 예산만큼 관찰을 담는데, 관찰은 수백 건씩 쌓여서 그대로 넣으면 대화가 묻히기 때문이다.

### 자기 자신은 찍히지 않는다

캐릭터가 캡처에 잡히면 자기 말풍선을 읽고 그것에 또 반응하는 고리가 생긴다. `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`로 자기 창을 캡처 대상에서 제외한다. 숨겼다 찍고 다시 보이는 방식과 달리 깜빡임이 없다.

## 실행하기

### 준비물

- Windows 10 2004 이상
- Python 3.12+
- [Ollama](https://ollama.com) — 로컬 비전 모델 실행
- Unity 6 LTS — 본체를 직접 빌드할 경우
- 클라우드 모델 API 키, [Fish Audio](https://fish.audio) API 키

### 프록시

```bash
cd server
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt

ollama pull qwen3-vl:8b-instruct
```

`.env` 파일에 클라우드 API 키를 넣는다.

```
OLLAMA_CLOUD_API_KEY=your-key-here
```

`profile.example.txt`를 `profile.txt`로 복사해 자신에 대한 내용을 적는다. 비워 둬도 동작한다.

```bash
python proxy_server.py
```

`http://127.0.0.1:8000/docs`에서 API를, `http://127.0.0.1:8000/viewer`에서 기록 뷰어를 볼 수 있다.

### 본체

Unity로 `client`를 열어 빌드한다. 빌드 전에 Player Settings에서 아래를 확인한다.

- Graphics APIs에서 **Direct3D12 제거** (투명 창이 동작하지 않는다)
- **Use DXGI flip model swapchain for D3D11** 해제
- **Run In Background** 켜기
- Fullscreen Mode를 **Windowed**로

빌드한 실행 파일과 같은 폴더에 `fish_key.txt`를 만들고 TTS API 키를 한 줄 적는다. 없으면 음성 없이 동작한다.

### 조작

| 동작 | 방법 |
|---|---|
| 말 걸기 | 캐릭터 클릭 |
| 대화창 열기 | `Ctrl` + `Alt` + `M` |
| 가리기 | `Ctrl` + `Alt` + `B` |
| 설정 메뉴 | 캐릭터 우클릭 |
| 옮기기 / 던지기 | 캐릭터를 끌기 |

## 캐릭터 그림 작업

메디방 페인트로 그린 `.mdp`를 레이어 구조 그대로 PNG로 뽑는다.

```
📁 body     idle, walk_01, walk_02
📁 eyes     open, closed
📁 mouth    closed, half, open
```

```bash
python tools/mdp_split.py mijin.mdp client/Assets/Sprites
```

폴더 안의 항목 하나가 PNG 한 장이 된다. 모든 PNG는 원래 캔버스 크기 그대로 저장되므로 Unity에서 겹치기만 하면 위치가 맞는다. Unity의 임포트 설정은 `.meta` 파일에 남으므로, 그림을 고치고 다시 뽑아도 설정은 유지된다.

## 알아둘 것

이 프로그램은 **사용자의 화면을 캡처해 외부 AI 서비스로 전송한다.** 위에 적은 필터들이 있지만 완전하지 않다. 민감한 작업을 할 때는 가리기를 켜는 것을 권한다.

캐릭터 그림과 설정은 직접 만든 것이다. 음성은 별도 서비스를 사용하므로 해당 서비스의 약관을 따른다.

## 라이선스

소스 코드는 MIT (`LICENSE`).

캐릭터 미진의 그림과 설정은 직접 만든 것으로, 재사용을 허락하지 않습니다.
- `sprite/` — 메디방 원본
- `client/Assets/Sprites/` — 분리된 PNG
- `persona.txt` — 캐릭터 설정

번들된 Pretendard 폰트는 SIL OFL 1.1을 따릅니다
(`client/Assets/Fonts/Pretendard - OFL.txt`).

투명 창 구현에 [UniWindowController](https://github.com/kirurobo/UniWindowController)(MIT)를
사용합니다. Package Manager로 설치되며 저장소에 포함되어 있지 않습니다.
