"""요청 계측 (요청 하나당 한 줄).

mem/ 과 다른 성격이다 - 이건 기억이 아니라 계측이라 STATS_FILE을 mem/ 밖에
둔다 (/mijin/reset이나 회고가 건드리면 안 된다).

기록에는 숫자와 분류만 담는다. 창 제목·OCR 텍스트·장면 설명·대사·사용자
발화는 절대 넣지 않는다 - 집계 화면이 127.0.0.1이라도 파일은 디스크에
평문으로 남는다. 창 종류가 필요하면 deep(작업창 여부)과 path로 충분하다.

"skipped"가 세 가지 다른 일(의도한 절약 / 프라이버시 차단 / 장애)을 뜻하므로,
outcome과 path를 분리해서 본다 - 장애가 절약으로 둔갑하면 안 된다.

집계·/stats 라우터는 다음 커밋에서 붙는다 (여기는 기록만).
"""

import json
from pathlib import Path
from typing import Any, Dict

from config import STATS_FILE


def record(rec: Dict[str, Any]) -> None:
    """한 줄 append. 실패해도 요청을 막지 않는다 - 콘솔에 경고만."""
    try:
        path = Path(STATS_FILE)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    except OSError as e:
        print(f"[STATS] 기록 실패: {e}")
