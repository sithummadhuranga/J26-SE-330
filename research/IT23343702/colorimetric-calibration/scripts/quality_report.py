"""Apply the capture quality gate to photos: CV's sticker finder -> the grey-patch check -> a verdict per photo.

    python scripts/quality_report.py --photos DIR [--photos DIR2 ...] [--labels labels.csv] --out table.csv

Writes one row per photo: finder code, stickers found, best frame/orientation contrast (0..1), marker code, verdict level, code,
English sentence, seconds. With several stickers the photo is judged by the best one (a sticker whose grey patch was accepted;
else the largest). Skin pixels are not evaluated: these are photos of the printed sheet, there is no skin in them.
Thresholds come from our own photos; nothing here is validated on patients.
"""

from __future__ import annotations

import argparse
import csv
import importlib.util
import sys
import time
from pathlib import Path

import cv2

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from colorimetric import marker as M  # noqa: E402
from colorimetric import quality as Q  # noqa: E402

FINDER_PATH = ROOT.parents[1] / "IT23294066" / "area-measurement" / "sticker_finder.py"
PHOTO_EXTENSIONS = (".jpg", ".jpeg", ".png")
COLUMNS = ("tag", "file", "finder_code", "n_stickers", "best_frame_contrast", "best_orientation_contrast", "marker_code",
           "level", "code", "message", "seconds")


def load_finder(path: Path = FINDER_PATH):
    spec = importlib.util.spec_from_file_location("sticker_finder", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_FINDER = None


def assess_photo(rgb, finder=None, skin_pixels: int | None = None) -> dict:
    """The gate on one uint8 RGB photo. `finder` is CV's sticker_finder module (loaded on first use)."""
    global _FINDER
    if finder is None:
        _FINDER = _FINDER or load_finder()
        finder = _FINDER
    t0 = time.perf_counter()
    diag = finder.diagnose_stickers(rgb)
    marker_code = None
    if diag.stickers:
        checks = [M.estimate_gains(s.crop) for s in diag.stickers]   # largest first
        best = next((c for c in checks if c.used), checks[0])
        marker_code = best.code
    v = Q.assess(diag, M.MarkerResult(marker_code == "", code=marker_code or "") if marker_code is not None else None, skin_pixels)
    return dict(finder_code=diag.code, n_stickers=len(diag.stickers), best_frame_contrast=round(diag.best_frame_contrast, 3),
                best_orientation_contrast=round(diag.best_orientation_contrast, 3), marker_code=marker_code, level=v.level, code=v.code,
                message=v.message, seconds=round(time.perf_counter() - t0, 2))


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--photos", type=Path, action="append", required=True, help="a folder of photos (repeat for several)")
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--tag-from", choices=["folder"], default="folder", help="the tag column holds the folder name")
    args = ap.parse_args(argv)
    finder = load_finder()
    rows = []
    for d in args.photos:
        for f in sorted(p for p in Path(d).iterdir() if p.suffix.lower() in PHOTO_EXTENSIONS):
            bgr = cv2.imread(str(f))        # applies the EXIF rotation
            if bgr is None:
                print(f"cannot read {f}", file=sys.stderr)
                continue
            rows.append(dict(tag=Path(d).name, file=f.name, **assess_photo(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB), finder)))
    with args.out.open("w", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=COLUMNS)
        w.writeheader()
        w.writerows(rows)
    counts: dict[str, int] = {}
    for r in rows:
        counts[r["level"]] = counts.get(r["level"], 0) + 1
    print(f"{len(rows)} photos -> {args.out}: " + ", ".join(f"{k} {v}" for k, v in sorted(counts.items())))
    return 0 if rows else 1


if __name__ == "__main__":
    sys.exit(main())
