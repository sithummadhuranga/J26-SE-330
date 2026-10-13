"""Prepare a folder of images plus ground-truth masks for the on-device accuracy test, and score the same images in Python.

For every image in a split folder it writes ID.jpg, ID.gt.u8 (the DFUC mask thresholded at 127, row-major 0/1 bytes) and
a manifest.json that also holds the Python mean Dice, so the device result has something to be compared with.

    python make_accuracy_set.py --model m.tflite --images <split dir> --dfuc-train <DFUC2022_train_release> --out <dir>

The output contains DFUC2022 photos (research use only), so keep it outside the git repo.
"""
import argparse
import json
import os
import shutil
import sys

import cv2
import numpy as np

sys.path.insert(0, os.path.dirname(__file__))
import litert_reference as ref  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--images", required=True)
    ap.add_argument("--dfuc-train", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    from ai_edge_litert.interpreter import Interpreter
    it = Interpreter(model_path=args.model)
    it.allocate_tensors()
    os.makedirs(args.out, exist_ok=True)

    ids = sorted(f[:-4] for f in os.listdir(args.images) if f.endswith(".jpg"))
    dice, iou, inter_sum, pred_sum, gt_sum, missed = [], [], 0, 0, 0, 0
    for i in ids:
        bgr = cv2.imread(os.path.join(args.images, i + ".jpg"))
        assert bgr.shape[:2] == (ref.INPUT_H, ref.INPUT_W), f"{i} is not 640x480"
        gt = cv2.imread(os.path.join(args.dfuc_train, "DFUC2022_train_masks", i + ".png"), 0) > 127
        pred = ref.segment(it, cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))["union"].astype(bool)
        inter = int((pred & gt).sum())
        dice.append(2 * inter / (pred.sum() + gt.sum()))
        iou.append(inter / (pred.sum() + gt.sum() - inter))
        inter_sum, pred_sum, gt_sum = inter_sum + inter, pred_sum + int(pred.sum()), gt_sum + int(gt.sum())
        missed += int(pred.sum() == 0)
        shutil.copy(os.path.join(args.images, i + ".jpg"), os.path.join(args.out, i + ".jpg"))
        gt.astype(np.uint8).tofile(os.path.join(args.out, i + ".gt.u8"))

    manifest = dict(
        model=os.path.basename(args.model), conf=ref.CONF, ids=ids,
        python_mean_dice=float(np.mean(dice)), python_mean_iou=float(np.mean(iou)),
        python_pooled_dice=2 * inter_sum / (pred_sum + gt_sum), python_missed=missed,
    )
    json.dump(manifest, open(os.path.join(args.out, "manifest.json"), "w"), indent=1)
    print(f"{len(ids)} images; Python mean Dice {manifest['python_mean_dice']:.4f}, mean IoU "
          f"{manifest['python_mean_iou']:.4f}, pooled Dice {manifest['python_pooled_dice']:.4f}, missed {missed}")


if __name__ == "__main__":
    main()
