"""Writes the fixture photos and expected results for the Dart capture gate (mobile/lib/features/measurement/capture/sticker_gate.dart).

Each photo is 1200 pixels on its long side, so the Dart gate and diagnose_stickers(max_dim=1200) look at the same pixels. The expected
result comes from the Python finder: the code, how many stickers it accepted, the corners of each. The Dart tests compare against it.

Photos made from the simulation need no input. The real ones are our own photos of the printed sheet (no faces, no location: the
files are re-encoded here, which drops the metadata). Pass --photos-root to include them.

    python make_gate_fixtures.py --out mobile/test/measurement/golden/gate [--photos-root "<Data set>"]
"""
import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "area-measurement"))
import sticker_finder as SF  # noqa: E402

LONG = 1200

REAL = {  # name -> path under the photos root
    "real_iphone_tilt": "SHEET_area_test 3/SHEET_15_1.jpg",
    "real_pixel_flat": "SHEET_area_test 3/SHEET_0_5.jpg",
    "real_glare_a": "SHEET_area_test 2/not_usable_glare/PXL_20261022_095108446.PORTRAIT.ORIGINAL.jpg",
    "real_glare_b": "SHEET_area_test 2/not_usable_glare/PXL_20261022_095110913.PORTRAIT.ORIGINAL.jpg",
}


def shrink(rgb, long_side=LONG):
    h, w = rgb.shape[:2]
    f = long_side / max(h, w)
    return rgb if f == 1 else cv2.resize(rgb, (round(w * f), round(h * f)), interpolation=cv2.INTER_AREA)


def simulated():
    """name -> RGB photo, from the simulation (needs the test helpers)."""
    sys.path.insert(0, str(HERE.parent / "tests"))
    import common  # noqa: F401
    import area_simulation as S
    from test_sticker_finder import DiagnoseTest, photo

    t = DiagnoseTest()
    out = {}
    img, _ = photo(tilt=25, rot=33, ppm=10.0)
    out["sim_six_tilt25"] = img
    out["sim_one_ok"] = t.one_sticker()[0]
    out["sim_one_mirrored"] = t.one_sticker(lambda b: b[:, ::-1])[0]

    def no_square(b):
        b[75:165, 75:165] = 255
        return b

    out["sim_one_no_orientation"] = t.one_sticker(no_square)[0]
    img, hmm, _ = t.one_sticker()
    out["sim_one_cut"] = img[:, int(S.project(hmm, [[40, 40]])[0][0]):]
    out["sim_one_washed"] = np.clip(110 + 0.5 * t.one_sticker()[0].astype(np.float32), 0, 255).astype(np.uint8)
    out["blank"] = np.full((900, 1200, 3), 140, np.uint8)
    return out


def expected(rgb):
    d = SF.diagnose_stickers(rgb, max_dim=LONG)
    stickers = sorted(d.stickers, key=lambda s: (round(float(s.corners.mean(axis=0)[1]) / 50), float(s.corners.mean(axis=0)[0])))
    return {
        "width": int(rgb.shape[1]), "height": int(rgb.shape[0]),
        "code": d.code,
        "found": len(d.stickers),
        "candidates": int(d.candidates),
        "best_frame_contrast": round(d.best_frame_contrast, 3),
        "best_orientation_contrast": round(d.best_orientation_contrast, 3),
        "stickers": [{"corners": [[round(float(x), 1), round(float(y), 1)] for x, y in s.corners],
                      "px_per_mm": round(s.px_per_mm, 3)} for s in stickers],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--photos-root")
    args = ap.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    photos = {n: shrink(i) for n, i in simulated().items()}
    if args.photos_root:
        for n, rel in REAL.items():
            bgr = cv2.imread(str(Path(args.photos_root) / rel))   # applies the EXIF rotation; the saved PNG has no metadata
            if bgr is None:
                print("cannot read", rel)
                continue
            photos[n] = shrink(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
    meta = {}
    for n, rgb in photos.items():
        path = out / f"{n}.jpg"                                  # JPEG keeps the folder small; the expected result is read from the saved file
        cv2.imwrite(str(path), cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 85])
        rgb = cv2.cvtColor(cv2.imread(str(path)), cv2.COLOR_BGR2RGB)
        meta[n] = expected(rgb)
        print(f"{n:24s} {rgb.shape[1]}x{rgb.shape[0]}  code={meta[n]['code']}  found={meta[n]['found']}")
    (out / "gate_expected.json").write_text(json.dumps({"max_dim": LONG, "cases": meta}, indent=1))


if __name__ == "__main__":
    main()
