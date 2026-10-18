"""Run the colour calibration over the whole YOLO dataset, so a model can be trained on calibrated images.

Reads images/<split>/*.jpg from a dataset made by data-preparation/dfuc_split.py, applies one variant of the calibration
pipeline (research/IT23343702, Color Matric's part), and writes a dataset of the same layout with lossless PNG images. Labels are
symlinked: the calibration keeps the image size, so the masks still fit.

    python calibrate_dataset.py --dataset <yolo_dfuc> --variant full --out <dir>

The output holds DFUC2022 photos (research use only), so keep it outside the git repo. manifest.json records the variant,
its settings, the calibration code's commit and how many images were classed as each skin type.
"""
import argparse
import collections
import dataclasses
import json
import os
import subprocess
import sys

import cv2

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
sys.path.insert(0, os.path.join(REPO, "research", "IT23343702", "colorimetric-calibration", "src"))
from colorimetric import pipeline as P  # noqa: E402
from colorimetric.api import CalibrationPipeline  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dataset", required=True)
    ap.add_argument("--variant", required=True, choices=sorted(P.ABLATION_VARIANTS))
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    config = P.ABLATION_VARIANTS[args.variant]
    pipeline = CalibrationPipeline(config=config)
    commit = subprocess.run(["git", "-C", REPO, "log", "-1", "--format=%H", "--", "research/IT23343702"],
                            capture_output=True, text=True).stdout.strip()
    stats = {}
    for split in ("train", "val", "test"):
        src = os.path.join(args.dataset, "images", split)
        img_out = os.path.join(args.out, "images", split)
        lab_out = os.path.join(args.out, "labels", split)
        os.makedirs(img_out, exist_ok=True)
        os.makedirs(lab_out, exist_ok=True)
        fst, ccm, n = collections.Counter(), 0, 0
        for name in sorted(os.listdir(src)):
            if not name.endswith(".jpg"):
                continue
            stem = name[:-4]
            rgb = cv2.cvtColor(cv2.imread(os.path.join(src, name)), cv2.COLOR_BGR2RGB)
            res = pipeline.process(rgb)
            assert res.calibrated_image.shape == rgb.shape
            cv2.imwrite(os.path.join(img_out, stem + ".png"), cv2.cvtColor(res.calibrated_image, cv2.COLOR_RGB2BGR))
            link = os.path.join(lab_out, stem + ".txt")
            if os.path.lexists(link):
                os.remove(link)
            os.symlink(os.path.realpath(os.path.join(args.dataset, "labels", split, stem + ".txt")), link)
            fst[res.fitzpatrick_class or "None"] += 1
            ccm += int(res.ccm_applied)
            n += 1
        stats[split] = dict(images=n, ccm_applied=ccm, fitzpatrick=dict(sorted(fst.items())))
        print(split, stats[split], flush=True)

    with open(os.path.join(args.out, "data.yaml"), "w") as f:
        f.write(f"path: {os.path.abspath(args.out)}\ntrain: images/train\nval: images/val\ntest: images/test\n"
                "names:\n  0: ulcer\n")
    with open(os.path.join(args.out, "manifest.json"), "w") as f:
        json.dump(dict(variant=args.variant, config=dataclasses.asdict(config), calibration_commit=commit, splits=stats),
                  f, indent=1, default=str)


if __name__ == "__main__":
    main()
