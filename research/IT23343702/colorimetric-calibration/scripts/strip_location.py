"""Copy photos for sharing with the GPS location removed. Nothing else changes: the image data is not re-encoded (the pixels stay
identical), and the orientation, capture time and camera model stay. The originals are never touched.

    python scripts/strip_location.py --photos DIR --out DIR2

Phone photos carry GPS tags even when the guideline says to switch location off (the area-test photos did). Only JPEG is handled: any other
file type is refused instead of being copied with its location. After copying, every file is read back; if a copy still has a location the
run fails.
Needs Pillow and piexif (tool dependencies, see requirements-scripts.txt).
"""

from __future__ import annotations

import argparse
import io
import sys
from dataclasses import dataclass
from pathlib import Path

import piexif
from PIL import Image

JPEG_EXTENSIONS = (".jpg", ".jpeg")


@dataclass(frozen=True)
class Stripped:
    had_location: bool


def _remove_gps(data: bytes) -> bytes:
    """The JPEG bytes with the GPS block removed from the EXIF segment (only that segment is rewritten)."""
    try:
        exif = piexif.load(data)
    except Exception:                      # no readable EXIF: nothing to remove, nothing to rewrite
        return data
    if not exif.get("GPS"):
        return data
    exif["GPS"] = {}
    out = io.BytesIO()
    piexif.insert(piexif.dump(exif), data, out)
    return out.getvalue()


def _has_location(data: bytes) -> bool:
    try:
        return bool(piexif.load(data).get("GPS"))
    except Exception:
        return False


def strip_location(src: Path, dst: Path) -> Stripped:
    src, dst = Path(src), Path(dst)
    if src.suffix.lower() not in JPEG_EXTENSIONS:
        raise ValueError(f"{src.name}: only JPEG is handled; other types would keep their location")
    if dst.exists() or dst.resolve() == src.resolve():
        raise FileExistsError(f"{dst} exists: refusing to overwrite")
    data = src.read_bytes()
    had = _has_location(data)
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_bytes(_remove_gps(data))
    return Stripped(had)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--photos", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    args = ap.parse_args(argv)
    n = with_location = 0
    failed = False
    for f in sorted(p for p in args.photos.iterdir() if p.suffix.lower() in JPEG_EXTENSIONS):
        dst = args.out / f.name
        r = strip_location(f, dst)
        n += 1
        with_location += r.had_location
        if _has_location(dst.read_bytes()):
            print(f"STILL HAS A LOCATION: {dst}", file=sys.stderr)
            failed = True
    print(f"{n} photos, {with_location} had a location -> {args.out}")
    return 1 if failed or n == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
