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


def select_instances(inst, conf_scores, conf, top_k=None, rel=None):
    """Instances to merge: those scoring at least `conf`; optionally only the `top_k` best, and only those scoring at least
    `rel` times the best kept score. Most DFUC photos show one ulcer, so extra instances are often false alarms."""
    keep = np.flatnonzero(conf_scores >= conf)
    if keep.size == 0:
        return inst[:0]
    keep = keep[np.argsort(-conf_scores[keep], kind="stable")]
    if rel is not None:
        keep = keep[conf_scores[keep] >= rel * conf_scores[keep[0]]]
    if top_k is not None:
        keep = keep[:top_k]
    return inst[keep]


def clean_mask(mask, fill_holes=False, min_area=0):
    """Optional clean-up of a binary mask: fill holes that are closed on all sides, drop connected blobs smaller than
    `min_area` pixels (the largest blob is always kept, so a found wound is never erased)."""
    out = mask.astype(bool)
    if min_area and out.any():
        n, lab, stats, _ = cv2.connectedComponentsWithStats(out.astype(np.uint8), connectivity=8)
        areas = stats[1:, cv2.CC_STAT_AREA]
        keep = {1 + int(k) for k in np.flatnonzero(areas >= min_area)} or {1 + int(np.argmax(areas))}
        out = np.isin(lab, list(keep))
    if fill_holes and out.any():
        contours, _ = cv2.findContours(out.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        filled = np.zeros(out.shape, np.uint8)
        cv2.drawContours(filled, contours, -1, 1, thickness=cv2.FILLED)
        out = filled.astype(bool)
    return out


def paired_bootstrap_diff(a, b, seed=0, n=5000):
    """Mean of a - b over images and its 95% bootstrap interval, resampling images in pairs."""
    a, b = np.asarray(a, float), np.asarray(b, float)
    if a.shape != b.shape:
        raise ValueError("paired scores must have the same length")
    d = a - b
    rng = np.random.default_rng(seed)
    means = [rng.choice(d, len(d)).mean() for _ in range(n)]
    return float(d.mean()), float(np.percentile(means, 2.5)), float(np.percentile(means, 97.5))


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


def score(records, conf, top_k=None, rel=None, fill_holes=False, min_area=0):
    rows = []
    for i, gt, inst, c in records:
        kept = select_instances(inst, c, conf, top_k=top_k, rel=rel)
        pred = kept.any(axis=0) if len(kept) else np.zeros_like(gt)
        if len(kept) and (fill_holes or min_area):
            pred = clean_mask(pred, fill_holes=fill_holes, min_area=min_area)
        d, j = dice_iou(pred, gt)
        rows.append(dict(id=i, dice=d, iou=j, gt_px=int(gt.sum()), pred_px=int(pred.sum()), n_pred=int(len(kept)),
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


POST_VARIANTS = (("plain", {}), ("top1", dict(top_k=1)), ("top2", dict(top_k=2)), ("rel0.5", dict(rel=0.5)),
                 ("fill", dict(fill_holes=True)), ("min20", dict(min_area=20)),
                 ("top1+fill", dict(top_k=1, fill_holes=True)))


def sweep_post(records, conf):
    """Print mean Dice for each post-processing variant and its paired difference against the plain run (95% interval)."""
    base = np.array([r["dice"] for r in score(records, conf)])
    print(f"conf {conf}, {len(records)} images")
    print("variant      meanDice  diff_vs_plain  95%_interval          missed")
    for name, opts in POST_VARIANTS:
        rows = score(records, conf, **opts)
        d = np.array([r["dice"] for r in rows])
        diff, lo, hi = paired_bootstrap_diff(d, base)
        print(f"{name:<12} {d.mean():.4f}   {diff:+.4f}        [{lo:+.4f}, {hi:+.4f}]   {sum(r['pred_px'] == 0 for r in rows)}")


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
    ap.add_argument("--top-k", type=int, default=None, help="merge only the K most confident instances")
    ap.add_argument("--rel", type=float, default=None, help="drop instances scoring below REL x the best instance")
    ap.add_argument("--fill-holes", action="store_true")
    ap.add_argument("--min-area", type=int, default=0, help="drop blobs smaller than this many pixels")
    ap.add_argument("--sweep-post", action="store_true",
                    help="with --conf: compare post-processing variants with the plain run on the same images")
    args = ap.parse_args()
    if not args.sweep and args.conf is None:
        ap.error("give --conf, or use --sweep on the validation split to choose it")
    if args.sweep_post and args.conf is None:
        ap.error("--sweep-post needs --conf")

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

    if args.sweep_post:
        sweep_post(records, args.conf)
        return

    opts = dict(top_k=args.top_k, rel=args.rel, fill_holes=args.fill_holes, min_area=args.min_area)
    rows = score(records, args.conf, **opts)
    summary = summarise(rows)
    summary.update(conf=args.conf, imgsz=args.imgsz, split_dir=args.split_dir, ultralytics=__version__,
                   weights_sha256=hashlib.sha256(open(args.weights, "rb").read()).hexdigest()[:16], **opts)
    print(json.dumps(summary, indent=1))
    if args.out:
        with open(args.out, "w") as f:
            json.dump(dict(summary=summary, per_image=rows), f, indent=1)


if __name__ == "__main__":
    main()
