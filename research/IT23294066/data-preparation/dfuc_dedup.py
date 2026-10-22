"""Find near-duplicate photos in the DFUC2022 training set, so they never land on both sides of a split.

DFUC2022 has no patient ids. With a random 80/20 split, two photos of the same ulcer can end up in train and validation,
which makes the validation score look better than it is. Photos of the same scene are grouped using SIFT matches that
survive RANSAC. It can't catch the same patient photographed on a different visit, so say that wherever the split is
described.

    python dfuc_dedup.py --images <DFUC2022_train_images> --out <clusters.json>

The match threshold comes from random pairs of unrelated photos: it sits above the highest count seen there. Both
distributions are printed.
"""
import argparse
import json
import os
import random

import cv2
import numpy as np


def global_descriptor(img):
    """Cheap whole-image signature, only used to pick candidate pairs. SIFT does the real check."""
    small = cv2.resize(img, (32, 24), interpolation=cv2.INTER_AREA)
    gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY).astype(np.float32).ravel()
    gray = (gray - gray.mean()) / (gray.std() + 1e-6)
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    hist = cv2.calcHist([hsv], [0, 1], None, [12, 6], [0, 180, 0, 256]).ravel().astype(np.float32)
    hist /= hist.sum() + 1e-6
    return gray / np.linalg.norm(gray), hist / (np.linalg.norm(hist) + 1e-6)


def sift_inliers(a, b, matcher):
    ka, da = a
    kb, db = b
    if da is None or db is None or len(ka) < 8 or len(kb) < 8:
        return 0
    good = [m for m, n in matcher.knnMatch(da, db, k=2) if m.distance < 0.75 * n.distance]
    if len(good) < 8:
        return 0
    src = np.float32([ka[m.queryIdx] for m in good])
    dst = np.float32([kb[m.trainIdx] for m in good])
    _, mask = cv2.estimateAffinePartial2D(src, dst, method=cv2.RANSAC, ransacReprojThreshold=4)
    return 0 if mask is None else int(mask.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--images", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--neighbours", type=int, default=8)
    ap.add_argument("--negatives", type=int, default=3000)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    names = sorted(f for f in os.listdir(args.images) if f.endswith(".jpg"))
    sift = cv2.SIFT_create(nfeatures=800)
    matcher = cv2.BFMatcher()
    feats, gdesc, hdesc = [], [], []
    for n in names:
        img = cv2.imread(os.path.join(args.images, n))
        g, h = global_descriptor(img)
        gdesc.append(g)
        hdesc.append(h)
        kps, des = sift.detectAndCompute(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), None)
        feats.append(([kp.pt for kp in kps], des))
    gdesc, hdesc = np.stack(gdesc), np.stack(hdesc)
    print(f"{len(names)} images described")

    sim = gdesc @ gdesc.T + hdesc @ hdesc.T
    np.fill_diagonal(sim, -9)
    cand = set()
    for i in range(len(names)):
        for j in np.argsort(-sim[i])[: args.neighbours]:
            cand.add((min(i, int(j)), max(i, int(j))))
    print(f"{len(cand)} candidate pairs to verify")
    cand_inl = {p: sift_inliers(feats[p[0]], feats[p[1]], matcher) for p in sorted(cand)}

    rng = random.Random(args.seed)
    neg = []
    while len(neg) < args.negatives:
        i, j = rng.randrange(len(names)), rng.randrange(len(names))
        if i != j and (min(i, j), max(i, j)) not in cand:
            neg.append(sift_inliers(feats[i], feats[j], matcher))
    neg = np.array(neg)
    threshold = int(neg.max()) + 5
    print(f"negative control ({len(neg)} random non-candidate pairs): median {int(np.median(neg))}, "
          f"p99 {int(np.percentile(neg, 99))}, max {int(neg.max())}  ->  duplicate threshold = {threshold} inliers")

    vals = np.array(list(cand_inl.values()))
    print(f"candidate pairs: median {int(np.median(vals))}, >= threshold: {(vals >= threshold).sum()}")

    parent = list(range(len(names)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    linked = [(p, v) for p, v in cand_inl.items() if v >= threshold]
    for (i, j), _ in linked:
        parent[find(i)] = find(j)
    groups = {}
    for i, n in enumerate(names):
        groups.setdefault(find(i), []).append(n)
    clusters = sorted(groups.values(), key=lambda g: g[0])
    sizes = np.bincount([len(c) for c in clusters])
    print(f"{len(clusters)} clusters; size histogram (size: count):",
          {s: int(c) for s, c in enumerate(sizes) if c})
    print(f"images in a cluster of size >= 2: {sum(len(c) for c in clusters if len(c) > 1)}")
    with open(args.out, "w") as f:
        json.dump({"threshold_inliers": threshold, "clusters": clusters}, f, indent=1)


if __name__ == "__main__":
    main()
