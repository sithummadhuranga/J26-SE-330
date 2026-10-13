"""Convert the DFUC2022 training masks to YOLO segmentation labels, and measure what the conversion loses.

The masks are anti-aliased 0..255 PNGs (the organisers smoothed them), not the 0/1 masks the dataset paper describes, so
they are thresholded first. Each connected component becomes one polygon: the organisers count one ulcer per component,
and we found 2304 ulcers and 2304 components. Polygons are simplified, and --sweep prints how much overlap with the
original mask each level loses, so the setting is measured instead of guessed.

    python dfuc_prepare.py --dfuc-train <DFUC2022_train_release> --sweep
    python dfuc_prepare.py --dfuc-train <DFUC2022_train_release> --labels-out <dir>

The images and masks are for research use only, so write the output outside the git repo.
"""
import argparse
import csv
import os

import cv2
import numpy as np

THRESHOLD = 127


def load_binary(path, threshold=THRESHOLD):
    m = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    return (m > threshold).astype(np.uint8)


def polygons(binary, epsilon):
    """One simplified polygon per connected component, largest first."""
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    out = []
    for c in contours:
        if epsilon > 0:
            c = cv2.approxPolyDP(c, epsilon, True)
        pts = c.reshape(-1, 2)
        if len(pts) >= 3:
            out.append(pts)
    return sorted(out, key=lambda p: -cv2.contourArea(p.astype(np.int32)))


def rasterise(polys, shape):
    canvas = np.zeros(shape, np.uint8)
    for p in polys:
        cv2.fillPoly(canvas, [p.astype(np.int32)], 1)
    return canvas


def dice(a, b):
    inter = int((a & b).sum())
    total = int(a.sum()) + int(b.sum())
    return 1.0 if total == 0 else 2 * inter / total


def label_lines(polys, width, height):
    return [
        "0 " + " ".join(f"{x / width:.6f} {y / height:.6f}" for x, y in p)
        for p in polys
    ]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dfuc-train", required=True, help="folder containing DFUC2022_train_images and DFUC2022_train_masks")
    ap.add_argument("--epsilon", type=float, default=0.5, help="polygon simplification in pixels (0.5 chosen with --sweep)")
    ap.add_argument("--sweep", action="store_true", help="only report fidelity for several epsilons and thresholds")
    ap.add_argument("--labels-out")
    ap.add_argument("--report", help="per-image CSV (ids, ulcer count, area, fidelity)")
    args = ap.parse_args()

    img_dir = os.path.join(args.dfuc_train, "DFUC2022_train_images")
    mask_dir = os.path.join(args.dfuc_train, "DFUC2022_train_masks")
    ids = sorted(f[:-4] for f in os.listdir(img_dir) if f.endswith(".jpg"))
    masks = {i: load_binary(os.path.join(mask_dir, i + ".png")) for i in ids}
    h, w = next(iter(masks.values())).shape

    if args.sweep:
        print(f"{len(ids)} masks, {w}x{h}, threshold {THRESHOLD}")
        print("epsilon  mean_Dice  p1_Dice  min_Dice  mean_points  worst_area_err_%")
        for eps in (0, 0.5, 1.0, 1.5, 2.0, 3.0):
            d, n, ae = [], [], []
            for i, m in masks.items():
                ps = polygons(m, eps)
                r = rasterise(ps, m.shape)
                d.append(dice(m, r))
                n.append(sum(len(p) for p in ps))
                ae.append(abs(int(r.sum()) - int(m.sum())) / max(int(m.sum()), 1) * 100)
            d = np.array(d)
            print(f"{eps:>7}  {d.mean():.4f}    {np.percentile(d, 1):.4f}   {d.min():.4f}    {np.mean(n):7.1f}      {max(ae):.2f}")
        print("\nthreshold sensitivity (area relative to threshold 127):")
        ref = {i: int(m.sum()) for i, m in masks.items()}
        for t in (64, 96, 160, 191):
            ch = []
            for i in ids:
                a = int(load_binary(os.path.join(mask_dir, i + ".png"), t).sum())
                ch.append((a - ref[i]) / max(ref[i], 1) * 100)
            ch = np.array(ch)
            print(f"  threshold {t}: median area change {np.median(ch):+.2f}%, p5 {np.percentile(ch, 5):+.2f}%, p95 {np.percentile(ch, 95):+.2f}%")
        return

    os.makedirs(args.labels_out, exist_ok=True)
    rows = []
    for i, m in masks.items():
        ps = polygons(m, args.epsilon)
        with open(os.path.join(args.labels_out, i + ".txt"), "w") as f:
            f.write("\n".join(label_lines(ps, w, h)) + "\n")
        r = rasterise(ps, m.shape)
        rows.append((i, len(ps), int(m.sum()), f"{dice(m, r):.5f}"))
    if args.report:
        with open(args.report, "w", newline="") as f:
            wr = csv.writer(f)
            wr.writerow(["id", "ulcers", "mask_pixels", "polygon_dice"])
            wr.writerows(rows)
    print(f"wrote {len(rows)} label files to {args.labels_out}; ulcers total {sum(r[1] for r in rows)}")


if __name__ == "__main__":
    main()
