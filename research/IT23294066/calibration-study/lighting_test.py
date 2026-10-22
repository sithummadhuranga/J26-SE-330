"""Lighting test of the sticker-based grey-patch correction (the colour component's `correct_with_marker`).

Question: when the light changes, does the raw wound model lose accuracy, and does the grey-patch correction win it back?
Setup (from the colour component's reply 2, T4, and reply 3-4; the rule below was fixed before any result was seen):
  * raw model A (seed 0), the 202 DFUC validation photos, confidence 0.25 (as chosen on validation). Test photos are not used.
  * a condition changes the photo in linear light per channel (gain), after an optional channel mixing, with an optional tone
    curve y = x^g on the encoded value; the SAME change is applied to a synthetic 500x500 sticker picture (stand-in colours
    of the sticker, Gaussian noise sigma 1.5 in 8-bit units on the patches only, as in the colour component's golden vectors).
  * "corrected" = correct_with_marker(changed photo, changed sticker picture); "raw" = the changed photo as it is.
  * paired bootstrap, 5000 resamples, per-image Dice.

Conditions (gain R,G,B): identity; minus2 x0.25; minus1 x0.5; plus1 x2; warm (1.25,1,0.8); cool (0.8,1,1.25); minus1_warm (0.625,0.5,0.4).
Stress sweeps (arbitrary, not typical camera values; run separately at identity gain, reported not judged): tone curve g in
{0.8, 0.9, 1.1, 1.25}; channel mixing e in {0.03, 0.06, 0.10} with I + e*M (M: zero diagonal, off-diagonals 1, rows renormalised).
Negative control (reported not judged): photo at minus1 but the sticker picture left unchanged, so the correction has nothing to find.

PASS RULE (written by the colour component, accepted by CV before any run):
  P0  a condition counts only if raw Dice on the changed photos is at least 0.03 below raw Dice on the originals.
  P1  identity: corrected minus raw within +-0.005 and the sticker accepted on at least 99% of photos.
  P2  each counted condition among minus1, warm, cool, minus1_warm: corrected minus raw >= +0.02 and the bootstrap lower bound > 0.
  P3  in the same conditions: corrected Dice within 0.02 of the original raw Dice (corrected >= original - 0.02).
  P4  sticker accepted on at least 95% of photos in every base condition except minus2.
  Reported, not judged: minus2 (grey gain about 4, at the limit), plus1 (white sheets clip, information is lost).
  Overall: PASS if P1, P2, P3, P4 all hold; FAIL if P1 fails or P2 fails in two or more counted conditions; otherwise UNCLEAR.
  If no judged condition is counted (the changes did not hurt the raw model enough), the result is UNCLEAR, not a pass.

Limit, stated in advance: the same change is applied to photo and sticker picture, so the correction can undo it almost
exactly. A pass shows that the arithmetic works and how much the raw model suffers. It does not show that real sticker
photos behave: real cameras add tone curves, mixing and clipping that this model only partly covers.

Writes only aggregates to --out (numbers, no photos). The changed photos go to a temporary folder that is deleted.

    python lighting_test.py --out <dir>/results.json [--limit N]
"""
import argparse
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

import cv2
import numpy as np

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / "research" / "IT23343702" / "colorimetric-calibration" / "src"))
sys.path.insert(0, str(REPO / "research" / "IT23343702" / "colorimetric-calibration" / "scripts"))
sys.path.insert(0, str(REPO / "research" / "IT23294066" / "segmentation" / "evaluation"))
from colorimetric import marker as M  # noqa: E402
import eval_seg as E  # noqa: E402
from ultralytics import YOLO  # noqa: E402

DERIVED = Path("/Users/sithummadhuranga/Desktop/Research/Data set/derived")
DFUC = Path("/Users/sithummadhuranga/Desktop/Research/Data set/DFUC/DFUC2022_train_release")
WEIGHTS = DERIVED / "runs" / "baseline_yolo11n_raw" / "weights" / "best.pt"
CONF, NOISE, N_STICKERS = 0.25, 1.5, 5
LABS = {"grey": (50, 0, 0), "skin_a": (38, 14, 14), "skin_b": (25, 8, 8)}  # stand-in colours, skin_b as in the golden vectors

BASE = {
    "identity": dict(gain=(1, 1, 1)), "minus2": dict(gain=(0.25, 0.25, 0.25)), "minus1": dict(gain=(0.5, 0.5, 0.5)),
    "plus1": dict(gain=(2, 2, 2)), "warm": dict(gain=(1.25, 1, 0.8)), "cool": dict(gain=(0.8, 1, 1.25)),
    "minus1_warm": dict(gain=(0.625, 0.5, 0.4)),
}
SWEEP = {**{f"curve_{g}": dict(gain=(1, 1, 1), curve=g) for g in (0.8, 0.9, 1.1, 1.25)},
         **{f"mix_{e}": dict(gain=(1, 1, 1), mix=e) for e in (0.03, 0.06, 0.10)}}
CONTROL = {"control_mismatch": dict(gain=(0.5, 0.5, 0.5), crop_gain=(1, 1, 1))}
JUDGED = ("minus1", "warm", "cool", "minus1_warm")


def mixing_matrix(e):
    m = np.eye(3) + e * (1 - np.eye(3))
    return m / m.sum(axis=1, keepdims=True)


def camera(rgb255, gain, curve=1.0, mix=0.0):
    """Colour as a camera records it, before noise and rounding: mixing and gain in linear light, encode, tone curve. Float 0-255."""
    lin = M._srgb_to_linear(np.asarray(rgb255, dtype=np.float64) / 255.0)
    if mix:
        lin = lin @ mixing_matrix(mix).T
    lin = np.clip(lin * np.asarray(gain), 0, None)
    enc = np.clip(M._linear_to_srgb(lin), 0, 1)
    return (enc ** curve) * 255.0


def change_photo(rgb, cond):
    return np.clip(np.rint(camera(rgb, cond["gain"], cond.get("curve", 1.0), cond.get("mix", 0.0))), 0, 255).astype(np.uint8)


def lab_to_srgb(lab):
    px = np.array(lab, dtype=np.float32).reshape(1, 1, 3)
    return np.clip(cv2.cvtColor(px, cv2.COLOR_LAB2RGB)[0, 0].astype(np.float64), 0, 1) * 255


def sticker(cond, seed, size=500):
    """Synthetic sticker picture with the stand-in colours, changed like the photo, noise only on the patches."""
    rng = np.random.default_rng(seed)
    img = np.full((size, size, 3), 20.0)
    img[size // 10: -size // 10, size // 10: -size // 10] = 240.0
    img[size // 10: size * 11 // 50, size // 10: size * 11 // 50] = 20.0
    layout = M.load_layout()
    gain = cond.get("crop_gain", cond["gain"])
    for name, (cx, cy, s) in layout.patches.items():
        half = int(s * size / 2)
        y0, y1, x0, x1 = int(cy * size) - half, int(cy * size) + half, int(cx * size) - half, int(cx * size) + half
        colour = camera(lab_to_srgb(cond.get("labs", LABS)[name]), gain, cond.get("curve", 1.0), cond.get("mix", 0.0))
        img[y0:y1, x0:x1] = colour + rng.normal(0, NOISE, (y1 - y0, x1 - x0, 3))
    return np.clip(np.rint(img), 0, 255).astype(np.uint8)


def boot(d, n=5000, seed=0):
    rng = np.random.default_rng(seed)
    m = np.array([d[rng.integers(0, len(d), len(d))].mean() for _ in range(n)])
    return float(np.percentile(m, 2.5)), float(np.percentile(m, 97.5))


def evaluate_rule(results, o):
    """The pass rule P0-P4 from the module header. results: condition -> raw, corrected, diff, diff_ci95, accepted; o: raw Dice on
    the original photos. Returns dict(P=..., counted=..., verdict=...). The identity check (P1) is looked at first, so an
    'unclear' result cannot hide that it failed."""
    counted = {c: results[c]["raw"] <= o - 0.03 for c in JUDGED}
    p = {"P0": dict(counted)}
    r = results["identity"]
    p["P1"] = abs(r["corrected"] - r["raw"]) <= 0.005 and r["accepted"] >= 0.99
    p2 = {c: (results[c]["diff"] >= 0.02 and results[c]["diff_ci95"][0] > 0) for c in JUDGED if counted[c]}
    p3 = {c: results[c]["corrected"] >= o - 0.02 for c in JUDGED if counted[c]}
    p["P2"], p["P3"] = p2, p3
    p["P4"] = all(results[c]["accepted"] >= 0.95 for c in BASE if c != "minus2")
    if not p["P1"]:
        verdict = "FAIL (P1: the identity light changed the result)"
    elif not any(counted.values()):
        verdict = "UNCLEAR (no judged condition hurt the raw model by 0.03 or more)"
    elif sum(1 for v in p2.values() if not v) >= 2:
        verdict = "FAIL"
    elif all(p2.values()) and all(p3.values()) and p["P4"]:
        verdict = "PASS"
    else:
        verdict = "UNCLEAR"
    return dict(P=p, counted=counted, verdict=verdict)


def self_check():
    """The synthetic sticker must give the same gains as the colour component's own golden-vector sticker."""
    import make_golden_vectors as G  # noqa: E402
    worst = 0.0
    for name in ("minus1", "warm", "minus1_warm"):
        mine = M.estimate_gains(sticker(BASE[name], 0))
        theirs = M.estimate_gains(G.sticker(gain=BASE[name]["gain"]))
        assert mine.used and theirs.used, (name, mine.reason, theirs.reason)
        worst = max(worst, float(np.abs(mine.gains - theirs.gains).max()))
    assert worst < 0.01, f"synthetic sticker disagrees with the golden-vector sticker (max gain difference {worst:.4f})"
    return worst


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=None, help="only the first N validation photos (smoke test, not a result)")
    args = ap.parse_args()
    val_dir = DERIVED / "yolo_dfuc" / "images" / "val"
    ids = sorted(f[:-4] for f in os.listdir(val_dir) if f.endswith(".jpg"))[: args.limit]
    mask_dir = DFUC / "DFUC2022_train_masks"
    device = E.pick_device()
    model = YOLO(str(WEIGHTS))
    print("self-check: largest gain difference to the golden-vector sticker = %.5f" % self_check(), flush=True)

    def dice_of(paths):
        return np.array([r["dice"] for r in E.score(E.collect(model, paths, device, 640), CONF)])

    orig = dice_of([dict(id=i, image=str(val_dir / f"{i}.jpg"), mask=str(mask_dir / f"{i}.png")) for i in ids])
    print(f"original photos: mean Dice {orig.mean():.4f} on {len(ids)} photos (the notes say 0.7359 on all 202)", flush=True)
    photos = {i: cv2.cvtColor(cv2.imread(str(val_dir / f"{i}.jpg")), cv2.COLOR_BGR2RGB) for i in ids}
    results = {"original": {"mean_dice": float(orig.mean()), "n": len(ids)}}
    tmp = Path(tempfile.mkdtemp(prefix="lighting_test_"))
    try:
        for name, cond in {**BASE, **SWEEP, **CONTROL}.items():
            stickers = [sticker(cond, k) for k in range(N_STICKERS)]
            sets = {"raw": [], "corrected": []}
            used, reasons = 0, {}
            for n, i in enumerate(ids):
                changed = change_photo(photos[i], cond)
                fixed, res = M.correct_with_marker(changed, stickers[n % N_STICKERS])
                used += int(res.used)
                if not res.used:
                    reasons[res.reason.split(" (")[0]] = reasons.get(res.reason.split(" (")[0], 0) + 1
                for kind, img in (("raw", changed), ("corrected", fixed)):
                    d = tmp / kind
                    d.mkdir(exist_ok=True)
                    cv2.imwrite(str(d / f"{i}.png"), cv2.cvtColor(img, cv2.COLOR_RGB2BGR))
            dice = {k: dice_of([dict(id=i, image=str(tmp / k / f"{i}.png"), mask=str(mask_dir / f"{i}.png")) for i in ids])
                    for k in sets}
            diff = dice["corrected"] - dice["raw"]
            lo, hi = boot(diff)
            results[name] = dict(raw=float(dice["raw"].mean()), corrected=float(dice["corrected"].mean()), diff=float(diff.mean()),
                                 diff_ci95=[lo, hi], accepted=used / len(ids), rejections=reasons)
            print(f"{name:17s} raw {dice['raw'].mean():.4f}  corrected {dice['corrected'].mean():.4f}  diff {diff.mean():+.4f} "
                  f"[{lo:+.4f}, {hi:+.4f}]  accepted {used}/{len(ids)} {reasons if reasons else ''}", flush=True)
            shutil.rmtree(tmp / "raw"); shutil.rmtree(tmp / "corrected")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    o = float(orig.mean())
    rule = evaluate_rule(results, o)
    p, counted, verdict = rule["P"], rule["counted"], rule["verdict"]
    results["rule"] = rule
    print("\nRULE:", json.dumps(p, default=str))
    print("VERDICT:", verdict, flush=True)
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(results, indent=1, default=str) + "\n")


if __name__ == "__main__":
    main()
