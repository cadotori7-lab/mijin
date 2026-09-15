import sys

# 가장 먼저 실행돼야 한다: vision.py를 임포트하는 순간 RapidOCR 초기화 로그가
# 한글로 찍히는데, 인코딩 설정 전이면 콘솔에서 깨진다.
sys.stdout.reconfigure(encoding='utf-8')

from fastapi import FastAPI
import uvicorn

from viewer import router as viewer_router
from mijin import router as mijin_router
from config import (
    OCR_BUDGET_DEEP, OCR_BUDGET_NORMAL,
    OCR_CROP_ENABLED, CROP_PROFILES,
    ENABLE_OBSERVATION_LOG, OBSERVATION_LOG, OBSERVE_BLOCKLIST,
    SKIP_VISION_MIN_OCR,
)
from vision import _HAS_OCR, _HAS_PYGETWINDOW, _HAS_IMAGEHASH
from context import _load_profile

app = FastAPI()
app.include_router(viewer_router)
app.include_router(mijin_router)


if __name__ == "__main__":
    print("[START] 미진이 프록시 서버 시작 (Port: 8000)")
    print(f"[OPTION] OCR 그라운딩: {'ON' if _HAS_OCR else 'OFF (pip install rapidocr onnxruntime)'}")
    print(f"[OPTION] 활성 창 감지: {'ON' if _HAS_PYGETWINDOW else 'OFF (pip install pygetwindow)'}")
    print(f"[OPTION] 화면 변화 감지 스킵: {'ON' if _HAS_IMAGEHASH else 'OFF (pip install imagehash pillow)'}")
    print(f"[OPTION] OCR 예산: 작업창 {OCR_BUDGET_DEEP}자 / 그 외 {OCR_BUDGET_NORMAL}자")
    if OCR_CROP_ENABLED:
        print(f"[OPTION] OCR 크롭: ON (프로파일 {len(CROP_PROFILES)}개 + 기본값)")
        for k, v in CROP_PROFILES.items():
            print(f"           - {k}: {v}")
    else:
        print("[OPTION] OCR 크롭: OFF")
    print(f"[OPTION] 관찰 로그: {'ON -> ' + OBSERVATION_LOG if ENABLE_OBSERVATION_LOG else 'OFF'}")
    if ENABLE_OBSERVATION_LOG:
        print(f"[OPTION] 관찰 제외 창: {len(OBSERVE_BLOCKLIST)}개 ({', '.join(OBSERVE_BLOCKLIST[:4])} ...)")
    print(f"[OPTION] 비전 생략 임계값: OCR {SKIP_VISION_MIN_OCR}자 이상 + 텍스트 전용 창")
    _profile_len = len(_load_profile())
    print(f"[OPTION] 프로필 주입: {'ON -> ' + str(_profile_len) + '자' if _profile_len else 'OFF (profile.txt 없음)'}")

    import memory
    memory.load()

    uvicorn.run(app, host="127.0.0.1", port=8000)
