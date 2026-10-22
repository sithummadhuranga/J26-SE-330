"""Does the grey-patch correction keep the skin type stable when the light changes?

Background: the colour component's skin-type estimate (ITA) is read from raw skin pixels, so it moves with exposure and
colour cast (on DFUC the camera was exposed for the white sheets, and 82% of validation photos came out as Fitzpatrick IV-VI).
The grey patch is meant to remove exactly that. This test measures how much the ITA angle and the Fitzpatrick class move
when the light changes, with and without the correction. No wound model is involved.

Setup: the 202 DFUC validation photos. A condition changes a photo in linear light (as in lighting_test.py) and a synthetic
500x500 sticker picture the same way (stand-in colours, noise sigma 1.5 on the patches). "raw" = ITA read on the changed
photo; "corrected" = ITA read after correct_with_marker. Reference for each method = its own reading at identity light
(raw at identity for raw, corrected at identity for corrected), so a method is judged on how much its own answer moves.
Class and angle come from colorimetric.ita.classify_image with its current default (skin-mask V >= 20).

RULE (fixed before any real run; the numbers are judgement, stated here so they cannot follow the result):
  S0  a condition counts only if the raw reading moves noticeably: median |ITA shift| >= 5 degrees or class change >= 20%.
  S1  identity light: corrected against raw on the same photos: median |ITA difference| <= 1 degree and class change <= 2%.
  S2  each counted condition among minus1, warm, cool, minus1_warm: corrected median shift <= half of the raw median shift,
      corrected class-change rate <= half of the raw rate, and the paired bootstrap lower bound of (raw rate - corrected
      rate) > 0.
  S3  in the same conditions: corrected median shift <= 3 degrees.
  S4  sticker accepted on at least 95% of photos in every base condition except minus2.
  Reported, not judged: minus2 and plus1, the tone-curve and channel-mixing sweeps (arbitrary values), the negative control
  (photo changed, sticker left unchanged), and the printed-grey error arms (the sticker grey is really lighter or darker than
  the reference L* 50, with the light unchanged): they measure how far a wrong printed grey moves the skin angle.
  Overall: PASS if S1, S2, S3, S4 all hold; FAIL if S1 fails or S2 fails in two or more counted conditions; otherwise
  UNCLEAR. If no judged condition counts, UNCLEAR.

Limit, stated in advance: the same change is applied to the photo and the sticker picture, so the corrected reading is close
to invariant by construction. The test shows how large the exposure sensitivity of the raw reading is and that the
arithmetic removes it; it does not show that real sticker photos behave.

Writes only aggregates (numbers) to --out.

    python ita_stability_test.py --out <dir>/ita_results.json [--limit N]
"""
import argparse
import json
import os
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import lighting_test as L  # noqa: E402  (conditions, change_photo, sticker, boot)
from colorimetric import marker as M  # noqa: E402
from colorimetric.ita import classify_image  # noqa: E402

GREY_ARMS = {f"grey_{d:+d}": dict(gain=(1, 1, 1), labs={**L.LABS, "grey": (50 + d, 0, 0)}) for d in (-6, -3, 3, 6)}
ORDER = ["I", "II", "III", "IV", "V", "VI"]


def read(rgb):
    """(ITA angle, class) or (nan, None) when the photo has too few skin pixels."""
    try:
        r = classify_image(rgb)
        return r.ita_angle, r.fitzpatrick_type.value
    except ValueError:
        return float("nan"), None


def rate_boot(flip_a, flip_b, n=5000, seed=0):
    """Paired bootstrap of mean(flip_a) - mean(flip_b)."""
    rng = np.random.default_rng(seed)
    d = flip_a.astype(float) - flip_b.astype(float)
    m = np.array([d[rng.integers(0, len(d), len(d))].mean() for _ in range(n)])
    return float(np.percentile(m, 2.5)), float(np.percentile(m, 97.5))


def evaluate_rule(results, s1_shift, s1_flip):
    """The pass rule S0-S4 from the module header. results: condition -> raw/corrected shift medians, class-change rates,
    flip_gain_ci95, accepted; s1_shift, s1_flip: corrected against raw at identity light (median |ITA difference|, class change).
    The identity check (S1) is looked at first."""
    p = {"S1": s1_shift <= 1.0 and s1_flip <= 0.02, "S1_values": dict(median_difference=s1_shift, class_change=s1_flip)}
    counted = {c: (results[c]["raw_shift_median"] >= 5.0 or results[c]["raw_flip"] >= 0.20) for c in L.JUDGED}
    s2, s3 = {}, {}
    for c in L.JUDGED:
        if not counted[c]:
            continue
        r = results[c]
        s2[c] = (r["cor_shift_median"] <= 0.5 * r["raw_shift_median"] and r["cor_flip"] <= 0.5 * r["raw_flip"]
                 and r["flip_gain_ci95"][0] > 0)
        s3[c] = r["cor_shift_median"] <= 3.0
    p["S2"], p["S3"] = s2, s3
    p["S4"] = all(results[c]["accepted"] >= 0.95 for c in L.BASE if c != "minus2")
    if not p["S1"]:
        verdict = "FAIL (S1: the identity light changed the result)"
    elif not any(counted.values()):
        verdict = "UNCLEAR (no judged condition moved the raw reading enough to count)"
    elif sum(1 for v in s2.values() if not v) >= 2:
        verdict = "FAIL"
    elif all(s2.values()) and all(s3.values()) and p["S4"]:
        verdict = "PASS"
    else:
        verdict = "UNCLEAR"
    return dict(P=p, counted=counted, verdict=verdict)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--limit", type=int, default=None, help="only the first N photos (smoke test, not a result)")
    args = ap.parse_args()
    val_dir = L.DERIVED / "yolo_dfuc" / "images" / "val"
    ids = sorted(f[:-4] for f in os.listdir(val_dir) if f.endswith(".jpg"))[: args.limit]
    photos = [cv2.cvtColor(cv2.imread(str(val_dir / f"{i}.jpg")), cv2.COLOR_BGR2RGB) for i in ids]
    print("self-check: largest gain difference to the golden-vector sticker = %.5f" % L.self_check(), flush=True)

    def readings(cond):
        stickers = [L.sticker(cond, k) for k in range(L.N_STICKERS)]
        raw, cor, used = [], [], 0
        for n, p in enumerate(photos):
            changed = L.change_photo(p, cond)
            fixed, res = M.correct_with_marker(changed, stickers[n % L.N_STICKERS])
            used += int(res.used)
            raw.append(read(changed))
            cor.append(read(fixed))
        return raw, cor, used / len(photos)

    base_raw, base_cor, _ = readings(L.BASE["identity"])
    ref_raw_ita = np.array([a for a, _ in base_raw]); ref_raw_cls = [c for _, c in base_raw]
    ref_cor_ita = np.array([a for a, _ in base_cor]); ref_cor_cls = [c for _, c in base_cor]
    results = {"n": len(ids), "reference_class_counts_raw": {k: ref_raw_cls.count(k) for k in ORDER},
               "reference_class_counts_corrected": {k: ref_cor_cls.count(k) for k in ORDER}}
    print("identity light, raw class counts:", results["reference_class_counts_raw"], flush=True)
    print("%-17s %16s %16s %10s %10s  %s" % ("condition", "raw shift med/90", "cor shift med/90", "raw flip", "cor flip", "accepted"), flush=True)

    for name, cond in {**L.BASE, **L.SWEEP, **L.CONTROL, **GREY_ARMS}.items():
        raw, cor, acc = readings(cond)
        raw_ita = np.array([a for a, _ in raw]); cor_ita = np.array([a for a, _ in cor])
        sh_raw = np.abs(raw_ita - ref_raw_ita); sh_cor = np.abs(cor_ita - ref_cor_ita)
        fl_raw = np.array([c != r for (_, c), r in zip(raw, ref_raw_cls)]); fl_cor = np.array([c != r for (_, c), r in zip(cor, ref_cor_cls)])
        lo, hi = rate_boot(fl_raw, fl_cor)
        results[name] = dict(
            raw_shift_median=float(np.nanmedian(sh_raw)), raw_shift_p90=float(np.nanpercentile(sh_raw, 90)),
            cor_shift_median=float(np.nanmedian(sh_cor)), cor_shift_p90=float(np.nanpercentile(sh_cor, 90)),
            raw_flip=float(fl_raw.mean()), cor_flip=float(fl_cor.mean()), flip_gain_ci95=[lo, hi], accepted=acc,
            signed_cor_shift_median=float(np.nanmedian(cor_ita - ref_cor_ita)))
        r = results[name]
        print("%-17s %7.1f / %-6.1f %8.1f / %-6.1f %9.0f%% %9.0f%%  %4.0f%%" % (
            name, r["raw_shift_median"], r["raw_shift_p90"], r["cor_shift_median"], r["cor_shift_p90"],
            100 * r["raw_flip"], 100 * r["cor_flip"], 100 * acc), flush=True)

    d = np.abs(ref_cor_ita - ref_raw_ita)  # S1 input: identity light, corrected against raw
    s1_shift, s1_flip = float(np.nanmedian(d)), float(np.mean([a != b for a, b in zip(ref_raw_cls, ref_cor_cls)]))
    rule = evaluate_rule(results, s1_shift, s1_flip)
    p, counted, verdict = rule["P"], rule["counted"], rule["verdict"]
    results["rule"] = rule
    print("\nCOUNTED:", counted)
    print("RULE:", json.dumps(p, default=str))
    print("VERDICT:", verdict, flush=True)
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text(json.dumps(results, indent=1, default=str) + "\n")


if __name__ == "__main__":
    main()
