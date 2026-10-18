"""Score a trained model the way the DFUC2022 challenge does, plus the numbers this project needs.

All ulcers found in an image are merged into one binary mask at 640x480 and compared with the organisers' mask
(thresholded at 127, the same ground truth the labels came from). An image with no detection scores Dice 0. The challenge
report says entries were ranked by "average Dice", but we couldn't confirm from public sources whether that means the
mean of per-image scores or a pooled value, so both are reported.

    python eval_seg.py --weights best.pt --split-dir <yolo_dfuc>/images/val --dfuc-train <DFUC2022_train_release> --sweep
    python eval_seg.py --weights best.pt --split-dir <yolo_dfuc>/images/test --dfuc-train <...> --conf 0.25 --out test.json

Choose the confidence threshold on `val` with --sweep, then keep it fixed. Never sweep on `test`.
"""
import argparse
import hashlib
import json
import os

import cv2
import numpy as np
import torch
from ultralytics import YOLO, __version__

THRESHOLD = 127
SIZE_EDGES = (1342, 3184, 8086)  # wound-size quartile edges of the full training set (pixels), see data-preparation/dfuc_split.py


def pick_device():
    if torch.cuda.is_available():
        return 0
    return "mps" if torch.backends.mps.is_available() else "cpu"


def dice_iou(pred, gt):
    inter = int((pred & gt).sum())
    p, g = int(pred.sum()), int(gt.sum())
    union = p + g - inter
    return (2 * inter / (p + g) if p + g else 1.0), (inter / union if union else 1.0)


def bootstrap_ci(values, seed=0, n=2000):
    rng = np.random.default_rng(seed)
    means = [rng.choice(values, len(values)).mean() for _ in range(n)]
    return float(np.percentile(means, 2.5)), float(np.percentile(means, 97.5))


def collect(model, paths, device, imgsz, chunk=50):
    """Per image: ground truth mask and every predicted instance with its confidence (lowest useful threshold)."""
    out = []
    # With Ultralytics 8.4.176, one predict() call on ~400 images returned nothing after the first image (350 worked,
    # 400 did not). So predict in small chunks.
    for start in range(0, len(paths), chunk):
        part = paths[start:start + chunk]
        results = model.predict([p["image"] for p in part], imgsz=imgsz, conf=0.01, retina_masks=True,
                                device=device, verbose=False)
        assert len(results) == len(part), f"asked for {len(part)} predictions, got {len(results)}"
        for p, r in zip(part, results):
            gt = cv2.imread(p["mask"], cv2.IMREAD_GRAYSCALE) > THRESHOLD
            if r.masks is None:
                inst, conf = np.zeros((0,) + gt.shape, bool), np.zeros(0)
            else:
                inst = r.masks.data.cpu().numpy() > 0.5
                conf = r.boxes.conf.cpu().numpy()
            assert inst.shape[1:] == gt.shape, (inst.shape, gt.shape)
            out.append((p["id"], gt, inst, conf))
    # Every image has an ulcer, and at confidence 0.01 a trained model nearly always proposes something. If most images
    # have no candidate at all, inference is broken, so stop instead of printing a meaningless score.
    empty = sum(1 for _, _, inst, _ in out if inst.shape[0] == 0)
    if empty > 0.2 * len(out):
        raise RuntimeError(f"{empty} of {len(out)} images have no candidate detection at conf 0.01; "
                           "inference looks broken, refusing to score")
    return out


def score(records, conf):
    rows = []
    for i, gt, inst, c in records:
        keep = c >= conf
        pred = inst[keep].any(axis=0) if keep.any() else np.zeros_like(gt)
        d, j = dice_iou(pred, gt)
        rows.append(dict(id=i, dice=d, iou=j, gt_px=int(gt.sum()), pred_px=int(pred.sum()), n_pred=int(keep.sum()),
                         inter=int((pred & gt).sum())))
    return rows


def summarise(rows):
    d = np.array([r["dice"] for r in rows])
    j = np.array([r["iou"] for r in rows])
    gt = np.array([r["gt_px"] for r in rows])
    pr = np.array([r["pred_px"] for r in rows])
    inter = np.array([r["inter"] for r in rows])
    lo, hi = bootstrap_ci(d)
    bins = np.searchsorted(SIZE_EDGES, gt)
    by_size = {f"Q{b + 1}": dict(images=int((bins == b).sum()), mean_dice=float(d[bins == b].mean()))
               for b in range(4) if (bins == b).any()}
    area_err = np.abs(pr - gt) / gt * 100
    return dict(
        images=len(rows), mean_dice=float(d.mean()), dice_ci95=[lo, hi], mean_iou=float(j.mean()),
        pooled_dice=float(2 * inter.sum() / (gt.sum() + pr.sum())),
        images_missed=int((pr == 0).sum()), images_dice_below_0_5=int((d < 0.5).sum()),
        median_area_error_pct=float(np.median(area_err)), mean_area_error_pct=float(area_err.mean()),
        mean_dice_by_wound_size_quartile=by_size,
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", required=True)
    ap.add_argument("--split-dir", required=True, help="folder of images of one split (e.g. .../images/val)")
    ap.add_argument("--dfuc-train", required=True)
    ap.add_argument("--conf", type=float, default=None)
    ap.add_argument("--sweep", action="store_true")
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--ext", default=".jpg", help="image file extension in --split-dir (.png for calibrated images)")
    ap.add_argument("--out")
    ap.add_argument("--device", default=None)
    args = ap.parse_args()
    if not args.sweep and args.conf is None:
        ap.error("give --conf, or use --sweep on the validation split to choose it")

    mask_dir = os.path.join(args.dfuc_train, "DFUC2022_train_masks")
    ids = sorted(f[: -len(args.ext)] for f in os.listdir(args.split_dir) if f.endswith(args.ext))
    paths = [dict(id=i, image=os.path.join(args.split_dir, i + args.ext), mask=os.path.join(mask_dir, i + ".png")) for i in ids]
    device = args.device if args.device is not None else pick_device()
    model = YOLO(args.weights)
    records = collect(model, paths, device, args.imgsz)

    if args.sweep:
        print("conf   mean_Dice  pooled_Dice  mean_IoU  missed_imgs")
        best = (-1, None)
        for c in (0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.6):
            s = summarise(score(records, c))
            print(f"{c:<6} {s['mean_dice']:.4f}     {s['pooled_dice']:.4f}       {s['mean_iou']:.4f}    {s['images_missed']}")
            if s["mean_dice"] > best[0]:
                best = (s["mean_dice"], c)
        print(f"best confidence threshold on this split: {best[1]}")
        return

    rows = score(records, args.conf)
    summary = summarise(rows)
    summary.update(conf=args.conf, imgsz=args.imgsz, split_dir=args.split_dir, ultralytics=__version__,
                   weights_sha256=hashlib.sha256(open(args.weights, "rb").read()).hexdigest()[:16])
    print(json.dumps(summary, indent=1))
    if args.out:
        with open(args.out, "w") as f:
            json.dump(dict(summary=summary, per_image=rows), f, indent=1)


if __name__ == "__main__":
    main()
