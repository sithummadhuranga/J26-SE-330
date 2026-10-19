"""Write golden test vectors for the Dart port of the light version (tests/golden/).

Inputs are PNG files (synthetic: no real photos, so nothing here is covered by a data licence).
Expected outputs are produced by the Python code of this repo at the time of writing and are checked
by tests/test_golden.py. Regenerate only on purpose: a changed expected value means the spec changed.

Lab implementations (the expected values say which):
  *_opencv8  : cv2.cvtColor on uint8 images, L* = L8 * 100 / 255, a*, b* = v - 128. This is what
               `classify_image` and `estimate_gains` use. OpenCV computes it with fixed-point tables,
               so a port that uses the textbook float formulas will differ slightly.
  *_float    : CIE L*a*b*, D65, from float32 RGB in [0, 1] (cv2.cvtColor on float32).
The `ita_difference_deg` field shows how far apart the two are on each photo; use it to set a tolerance.

Usage: .venv/bin/python scripts/make_golden_vectors.py
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from colorimetric import marker as M  # noqa: E402
from colorimetric.ita import classify_image  # noqa: E402

OUT = ROOT / "tests" / "golden"
LAYOUT, REF = M.load_layout(), M.load_reference()


def lab_to_srgb(lab):
    px = np.array(lab, dtype=np.float32).reshape(1, 1, 3)
    return np.clip(cv2.cvtColor(px, cv2.COLOR_LAB2RGB)[0, 0].astype(np.float64), 0, 1) * 255


def camera(rgb255, gain, noise, rng):
    """Colour as a camera would record it: linear-light per-channel gain, clip, 8-bit."""
    lin = M._srgb_to_linear(np.asarray(rgb255, dtype=np.float64) / 255.0) * np.asarray(gain)
    return np.clip(M._linear_to_srgb(np.clip(lin, 0, None)) * 255.0 + rng.normal(0, noise, 3), 0, 255)


def sticker(gain=(1, 1, 1), size=500, noise=1.5, seed=0, grey_lab=(50, 0, 0), skin_a_lab=(38, 14, 14),
            skin_b_lab=(25, 8, 8), grey_noise=None):
    rng = np.random.default_rng(seed)
    img = np.full((size, size, 3), 20.0)
    img[size // 10: -size // 10, size // 10: -size // 10] = 240.0
    img[size // 10: size * 11 // 50, size // 10: size * 11 // 50] = 20.0  # orientation square (5-11 mm)
    labs = {"grey": grey_lab, "skin_a": skin_a_lab, "skin_b": skin_b_lab}
    for name, (cx, cy, s) in LAYOUT.patches.items():
        half = int(s * size / 2)
        y0, y1, x0, x1 = int(cy * size) - half, int(cy * size) + half, int(cx * size) - half, int(cx * size) + half
        n = grey_noise if (name == "grey" and grey_noise is not None) else noise
        for y in range(y0, y1):
            for x in range(x0, x1):
                img[y, x] = camera(lab_to_srgb(labs[name]), gain, n, rng)
    return np.clip(np.rint(img), 0, 255).astype(np.uint8)


def photo(rgb, seed, h=480, w=640, sheet=True):
    """640x480 stand-in photo: a skin region with texture, optional white sheet, dark corner."""
    rng = np.random.default_rng(seed)
    img = np.full((h, w, 3), 235.0) if sheet else np.full((h, w, 3), 30.0)
    yy, xx = np.mgrid[0:h, 0:w]
    inside = ((xx - w / 2) / (w * 0.38)) ** 2 + ((yy - h / 2) / (h * 0.38)) ** 2 < 1
    shade = 1 + 0.08 * np.sin(xx / 37.0) * np.cos(yy / 29.0)
    img[inside] = (np.asarray(rgb, dtype=np.float64) * shade[inside, None]) + rng.normal(0, 4, (inside.sum(), 3))
    img[:40, :60] = 10.0
    return np.clip(np.rint(img), 0, 255).astype(np.uint8)


def ita_both(rgb):
    lab8 = cv2.cvtColor(rgb, cv2.COLOR_RGB2LAB).astype(np.float64)
    labf = cv2.cvtColor(rgb.astype(np.float32) / 255.0, cv2.COLOR_RGB2LAB).astype(np.float64)
    return lab8, labf


def save(path: Path, rgb):
    path.parent.mkdir(parents=True, exist_ok=True)
    assert cv2.imwrite(str(path), cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))


def main() -> None:
    exp: dict = {
        "note": "Synthetic vectors. Gains are per-channel linear-light multipliers (R, G, B). Tolerances are suggestions.",
        "lab_implementation": {
            "estimate_gains": "median of the patch pixels -> cv2.cvtColor uint8 RGB2LAB (L*=L8*100/255, a,b=v-128) for the "
                              "a*b* neutrality and dE checks; target grey -> linear RGB via cv2 float32 LAB2RGB then sRGB decode.",
            "skin_type": "cv2.cvtColor uint8 RGB2LAB, mean over the HSV skin mask; HSV from cv2 uint8 (H 0-179 -> degrees x2).",
        },
        "constants": {"MAX_GREY_L_STD": M.MAX_GREY_L_STD, "MAX_CLIPPED_FRACTION": M.MAX_CLIPPED_FRACTION,
                      "GAIN_RANGE": list(M.GAIN_RANGE), "MIN_PATCH_PIXELS": M.MIN_PATCH_PIXELS,
                      "MAX_GREY_AB_ERROR": M.MAX_GREY_AB_ERROR, "MAX_SKIN_DELTA_E": M.MAX_SKIN_DELTA_E},
        "grey_gains": {}, "apply_gains": {}, "skin_type": {},
    }

    crops = {
        "c01_reference_colours": dict(),
        "c02_dim_exposure": dict(gain=(0.35, 0.35, 0.35)),
        "c03_warm_light": dict(gain=(1.5, 1.0, 0.65)),
        "c04_bright": dict(gain=(2.0, 2.0, 2.0)),
        "c05_cool_and_dim": dict(gain=(0.6, 0.8, 1.2)),
    }
    for name, kw in crops.items():
        img = sticker(**kw)
        save(OUT / "crops" / f"{name}.png", img)
        r = M.estimate_gains(img)
        assert r.used, (name, r.reason)
        exp["grey_gains"][name] = {"used": True, "gains": [round(float(g), 4) for g in r.gains],
                                   "skin_delta_e": {k: round(v, 2) for k, v in r.skin_delta_e.items()},
                                   "label": r.label, "gains_tolerance": 0.01}

    rejects = {
        "r01_grey_clipped": (sticker(gain=(8, 8, 8)), None, "clipped"),
        "r02_grey_uneven": (sticker(grey_noise=24.0), None, "not uniform"),
        "r03_too_small": (sticker(size=40), None, "pixels"),
        "r04_grey_not_neutral": (sticker(grey_lab=(50, -35, 35)), None, "not neutral"),
        "r05_gain_out_of_range": (sticker(gain=(0.08, 0.08, 0.08)), None, "out of range"),
        "r06_skin_patch_off": (sticker(skin_a_lab=(60, 35, 30)), "reference-photos", "skin_a patch off by dE"),
    }
    ref_photos = OUT / "reference_photos_example.json"
    ref_photos.write_text(json.dumps({"source": "reference-photos", "lab": {
        "grey": [50, 0, 0], "skin_a": [38, 14, 14], "skin_b": [25, None, None]}}, indent=1) + "\n")
    for name, (img, ref, why) in rejects.items():
        save(OUT / "crops" / f"{name}.png", img)
        r = M.estimate_gains(img, reference=M.load_reference(ref_photos) if ref else None)
        assert (not r.used) and why in r.reason, (name, r.reason)
        exp["grey_gains"][name] = {"used": False, "reason_contains": why, "reason": r.reason,
                                   "reference": "reference_photos_example.json" if ref else "stand-in (sticker/marker_reference.json)"}

    skins = {
        "p01_light_skin": (224, 172, 150), "p02_medium_skin": (190, 140, 112), "p03_tan_skin": (150, 105, 80),
        "p04_dark_skin": (112, 72, 52), "p05_very_dark_V_under_35": (64, 42, 30), "p06_black_shadow_only": (28, 20, 16),
    }
    for i, (name, rgb) in enumerate(skins.items()):
        img = photo(rgb, seed=10 + i)
        save(OUT / "photos" / f"{name}.png", img)
        entry: dict = {"size": [640, 480]}
        try:
            res = classify_image(img)
            l8, lf = ita_both(img)
            m = None
            from colorimetric.ita import skin_mask_hsv
            m = skin_mask_hsv(img)
            ita_f = float(np.degrees(np.arctan2(lf[..., 0][m].mean() - 50, lf[..., 2][m].mean())))
            entry.update({"error": None, "skin_pixel_count": res.skin_pixel_count, "ita_opencv8": round(res.ita_angle, 3),
                          "mean_L_opencv8": round(res.mean_L, 3), "mean_b_opencv8": round(res.mean_b, 3),
                          "ita_float": round(ita_f, 3), "ita_difference_deg": round(ita_f - res.ita_angle, 3),
                          "fitzpatrick": res.fitzpatrick_type.value, "gate_dark_at_28": bool(res.ita_angle <= 28.0),
                          "tolerance": {"ita_deg": 0.15, "pixel_count": 25}})
        except ValueError as e:
            entry.update({"error": "too few skin pixels", "message": str(e)})
        exp["skin_type"][name] = entry
    # same image with the old V limit, to pin the 20 vs 35 behaviour
    img = photo(skins["p05_very_dark_V_under_35"], seed=14)
    try:
        classify_image(img, v_min=35.0)
        v35 = {"error": None}
    except ValueError as e:
        v35 = {"error": "too few skin pixels", "message": str(e)}
    exp["skin_type"]["p05_very_dark_V_under_35"]["with_v_min_35"] = v35

    base = photo(skins["p02_medium_skin"], seed=11)
    for name, gains in {"g01_warm": [1.2, 1.0, 0.8], "g02_dim": [0.5, 0.5, 0.5], "g03_identity": [1.0, 1.0, 1.0]}.items():
        gains = np.array(gains)
        out = M.apply_gains(base, gains)
        save(OUT / "photos" / f"apply_{name}.png", out)
        levels = M._srgb_to_linear(np.arange(256) / 255.0)
        lut = [np.clip(np.rint(M._linear_to_srgb(np.clip(levels * g, 0, 1)) * 255.0), 0, 255).astype(int).tolist() for g in gains]
        exp["apply_gains"][name] = {"input": "photos/p02_medium_skin.png", "output": f"photos/apply_{name}.png",
                                    "gains": gains.tolist(), "lut_R_first_16": lut[0][:16], "lut_R_last_4": lut[0][-4:],
                                    "lut_G_at_[0,64,128,255]": [lut[1][i] for i in (0, 64, 128, 255)],
                                    "lut_B_at_[0,64,128,255]": [lut[2][i] for i in (0, 64, 128, 255)],
                                    "exact_match_expected": True}
    (OUT / "expected.json").write_text(json.dumps(exp, indent=1) + "\n")
    print("wrote", OUT, "-", len(list(OUT.rglob("*.png"))), "PNG files")


if __name__ == "__main__":
    main()
