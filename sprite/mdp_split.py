"""메디방 .mdp를 직접 읽어 폴더 구조대로 PNG로 분리한다.
사용법: python mdp_split.py 미진.mdp ./sprites
- 최상위 폴더(body, eyes, mouth ...) 안의 항목 하나가 PNG 한 장이 된다.
- 항목이 폴더면 그 안의 레이어를 합쳐 한 장으로 만든다.
- 이름이 _로 시작하는 폴더/레이어는 건너뛴다.
- 눈 표정처럼 그릴 때 숨겨두는 레이어도 있으므로 숨김 여부는 무시하고 전부 뽑는다.
- 모든 PNG는 원래 캔버스 크기 그대로 저장된다 (Unity에서 겹치기만 하면 위치가 맞음).
"""
import sys, struct, zlib, re
import xml.etree.ElementTree as ET
from pathlib import Path
from PIL import Image


def read_mdp(path):
    d = Path(path).read_bytes()
    if not d.startswith(b"mdipack"):
        raise ValueError("mdp 파일이 아닙니다")
    xs, xe = d.find(b"<?xml"), d.find(b"</Mdiapp>") + len("</Mdiapp>")
    root = ET.fromstring(d[xs:xe].decode("utf-8"))

    chunks, i = {}, xe
    while (i := d.find(b"PAC ", i)) >= 0:
        size = struct.unpack("<I", d[i + 4:i + 8])[0]
        name = re.search(rb"[a-z0-9]+img|thumb", d[i + 8:i + 132]).group().decode()
        chunks[name] = d[i + 132:i + size]
        i += size
    return root, chunks


def decode_layer(data, w, h):
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    if len(data) < 8:
        return img   # 빈 레이어 (메디방이 픽셀 없는 레이어는 내용을 저장하지 않음)
    cnt, ts = struct.unpack("<II", data[:8])
    p = 8
    for _ in range(cnt):
        tx, ty, _flag, sz = struct.unpack("<IIII", data[p:p + 16])
        p += 16
        raw = zlib.decompress(data[p:p + sz])
        p += sz + (-sz) % 4
        tile = Image.frombytes("RGBA", (ts, ts), raw, "raw", "BGRA")
        img.alpha_composite(tile, (tx * ts, ty * ts))
    return img


def main(src, out_dir):
    root, chunks = read_mdp(src)
    W, H = int(root.get("width")), int(root.get("height"))
    layers = root.find("Layers").findall("Layer")   # 파일 순서 = 아래 -> 위
    by_id = {l.get("id"): l for l in layers}
    order = {l.get("id"): n for n, l in enumerate(layers)}

    def children(pid):
        return sorted((l for l in layers if l.get("parentId") == pid),
                      key=lambda l: order[l.get("id")])

    def skip(l):
        return l.get("name", "").startswith("_")

    def render(l):
        if l.get("type") == "folder":
            img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            for c in children(l.get("id")):
                if not skip(c):
                    img.alpha_composite(render(c))
            return img
        if l.get("mode") != "normal" or l.get("clipping") == "true":
            print(f"  [주의] {l.get('name')}: 혼합모드/클리핑은 일반 합성으로 처리됨")
        img = decode_layer(chunks.get(l.get("bin"), b""), W, H)
        a = int(l.get("alpha", 255))
        if a < 255:
            img.putalpha(img.getchannel("A").point(lambda v: v * a // 255))
        return img

    out = Path(out_dir)
    for group in children("-1"):
        if group.get("type") != "folder" or skip(group):
            continue
        for item in children(group.get("id")):
            if skip(item):
                continue
            path = out / group.get("name") / f"{item.get('name')}.png"
            path.parent.mkdir(parents=True, exist_ok=True)
            render(item).save(path)
            print("저장:", path)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
