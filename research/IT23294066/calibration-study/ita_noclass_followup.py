"""Follow-up to ita_stability_test.py (post-hoc, asked for by the colour component): in the first test a photo with no skin class
(fewer than 100 skin-coloured pixels) is ignored by the angle shift columns but counted as a class change in the class-change
columns. This script reports, per condition and method, the share of photos with no class, and the class-change rate among the
photos that have a class both at identity light and under the condition. It does not change the verdict of the first test.

    python ita_noclass_followup.py --out <dir>/ita_noclass.json
"""
import argparse
import json
import os
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ita_stability_test as T  # noqa: E402
import lighting_test as L  # noqa: E402
from colorimetric import marker as M  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    val_dir = L.DERIVED / "yolo_dfuc" / "images" / "val"
    ids = sorted(f[:-4] for f in os.listdir(val_dir) if f.endswith(".jpg"))
    photos = [cv2.cvtColor(cv2.imread(str(val_dir / f"{i}.jpg")), cv2.COLOR_BGR2RGB) for i in ids]

    def readings(cond):
        stickers = [L.sticker(cond, k) for k in range(L.N_STICKERS)]
        raw, cor = [], []
        for n, p in enumerate(photos):
            changed = L.change_photo(p, cond)
            fixed, _ = M.correct_with_marker(changed, stickers[n % L.N_STICKERS])
            raw.append(T.read(changed)[1]); cor.append(T.read(fixed)[1])
        return raw, cor

    ref_raw, ref_cor = readings(L.BASE["identity"])
    out = {"identity_no_class": {"raw": sum(c is None for c in ref_raw) / len(ids), "corrected": sum(c is None for c in ref_cor) / len(ids)}}
    print("%-17s %14s %14s | class change among photos with a class both times: %-8s %-8s" % ("condition", "no class raw", "no class cor", "raw", "cor"), flush=True)
    for name, cond in {**L.BASE, **L.SWEEP, **L.CONTROL}.items():
        raw, cor = readings(cond)
        nr, nc = sum(c is None for c in raw) / len(ids), sum(c is None for c in cor) / len(ids)
        vr = [(a, b) for a, b in zip(raw, ref_raw) if a is not None and b is not None]
        vc = [(a, b) for a, b in zip(cor, ref_cor) if a is not None and b is not None]
        fr = float(np.mean([a != b for a, b in vr])) if vr else float("nan")
        fc = float(np.mean([a != b for a, b in vc])) if vc else float("nan")
        out[name] = dict(no_class_raw=nr, no_class_corrected=nc, flip_raw_valid_only=fr, flip_corrected_valid_only=fc, n_valid_raw=len(vr), n_valid_corrected=len(vc))
        print("%-17s %13.0f%% %13.0f%% | %31.0f%% %7.0f%%   (n %d / %d)" % (name, 100 * nr, 100 * nc, 100 * fr, 100 * fc, len(vr), len(vc)), flush=True)
    Path(args.out).write_text(json.dumps(out, indent=1) + "\n")


if __name__ == "__main__":
    main()
