"""Write test files for the Dart photo preparation (mobile/lib/features/measurement/image_prepare.dart).

From a 640x480 golden photo it makes three large photos (landscape, portrait and square, as a phone could take) and the
canvas the Python reference makes from each. The large photos are enlargements, so they have no real extra detail: this
tests the resizing and the padding rules, not how a real camera behaves.

    python make_resize_golden.py --golden <golden dir> --id 100020

The output holds a DFUC2022 photo (research use only), so it stays outside the git repo.
"""
import argparse
import json
import os
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(__file__))
import litert_reference as ref  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--golden", required=True)
    ap.add_argument("--id", default="100020")
    args = ap.parse_args()

    small = np.fromfile(os.path.join(args.golden, args.id + ".rgb.u8"), np.uint8).reshape(ref.INPUT_H, ref.INPUT_W, 3)
    cases = {
        "landscape": cv2.resize(small, (3200, 2400), interpolation=cv2.INTER_CUBIC),
        "portrait": cv2.resize(np.rot90(small).copy(), (2400, 3200), interpolation=cv2.INTER_CUBIC),
        "square": cv2.resize(small[:, 80:560].copy(), (2000, 2000), interpolation=cv2.INTER_CUBIC),
    }
    meta = {}
    for name, big in cases.items():
        canvas, scale, (left, top) = ref.letterbox(big)
        big.tofile(os.path.join(args.golden, f"resize_{name}.big.rgb.u8"))
        canvas.tofile(os.path.join(args.golden, f"resize_{name}.canvas.rgb.u8"))
        meta[name] = dict(width=int(big.shape[1]), height=int(big.shape[0]), scale=scale, pad_left=left, pad_top=top)
        print(name, meta[name])
    json.dump(meta, open(os.path.join(args.golden, "resize_manifest.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
