"""Build the YOLO dataset folders from the labels made by dfuc_prepare.py, with a split that keeps duplicates together.

Two steps, fixed seed:
  1. 20% of the images become `test`. That is the "20% internal validation split" from the proposal (sections 5.2 and
     5.5). It is only used for the final numbers.
  2. The other 80% is split into `train` (87.5%) and `val` (12.5%). `val` is for early stopping and choosing settings.
Duplicate groups from dfuc_dedup.py always stay on one side. Groups are stratified by wound-size quartile and by
one or several ulcers, so each split has a similar mix of small and large wounds.

Images are symlinked, not copied. Write the output outside the git repo: DFUC is for research use only.

    python dfuc_split.py --dfuc-train <DFUC2022_train_release> --labels <dir> --clusters <json> --report <csv> --out <dir>
"""
import argparse
import csv
import json
import os
import random

import numpy as np


def stratified_group_split(groups, strata, fraction, seed):
    """groups: list of lists of ids; strata: id -> key. Returns (held_out_ids, rest_ids), whole groups only."""
    rng = random.Random(seed)
    by_stratum = {}
    for g in groups:
        by_stratum.setdefault(strata[g[0]], []).append(g)
    held, rest = [], []
    for key in sorted(by_stratum):
        gs = by_stratum[key]
        rng.shuffle(gs)
        quota = round(fraction * sum(len(g) for g in gs))
        got = 0
        for g in gs:
            if got + len(g) / 2 <= quota:  # take the group if it brings us closer to the quota
                held.extend(g)
                got += len(g)
            else:
                rest.extend(g)
    return held, rest


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dfuc-train", required=True)
    ap.add_argument("--labels", required=True)
    ap.add_argument("--clusters", required=True)
    ap.add_argument("--report", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--seed", type=int, default=42)
    args = ap.parse_args()

    info = {}
    with open(args.report) as f:
        for r in csv.DictReader(f):
            info[r["id"]] = (int(r["ulcers"]), int(r["mask_pixels"]))
    clusters = [[n[:-4] for n in c] for c in json.load(open(args.clusters))["clusters"]]
    assert sorted(i for c in clusters for i in c) == sorted(info), "cluster file and label report disagree"

    areas = np.array([v[1] for v in info.values()], float)
    q = np.percentile(areas, [25, 50, 75])
    strata = {i: (int(np.searchsorted(q, a)), min(u, 2)) for i, (u, a) in info.items()}

    test_ids, rest_ids = stratified_group_split(clusters, strata, 0.20, args.seed)
    rest_set = set(rest_ids)
    rest_clusters = [[i for i in c if i in rest_set] for c in clusters if c[0] in rest_set]
    val_ids, train_ids = stratified_group_split(rest_clusters, strata, 0.125, args.seed + 1)
    splits = {"train": train_ids, "val": val_ids, "test": test_ids}

    where = {i: s for s, ids in splits.items() for i in ids}
    assert len(where) == len(info) and sum(len(v) for v in splits.values()) == len(info)
    for c in clusters:
        assert len({where[i] for i in c}) == 1, f"duplicate group split across sets: {c}"

    img_dir = os.path.join(args.dfuc_train, "DFUC2022_train_images")
    for s, ids in splits.items():
        for sub in ("images", "labels"):
            os.makedirs(os.path.join(args.out, sub, s), exist_ok=True)
        for i in sorted(ids):
            for sub, src, ext in (("images", os.path.join(img_dir, i + ".jpg"), ".jpg"),
                                  ("labels", os.path.join(args.labels, i + ".txt"), ".txt")):
                dst = os.path.join(args.out, sub, s, i + ext)
                if os.path.lexists(dst):
                    os.remove(dst)
                os.symlink(os.path.abspath(src), dst)
        with open(os.path.join(args.out, f"{s}.txt"), "w") as f:
            f.write("\n".join(sorted(ids)) + "\n")
    with open(os.path.join(args.out, "data.yaml"), "w") as f:
        f.write(f"path: {os.path.abspath(args.out)}\ntrain: images/train\nval: images/val\ntest: images/test\n"
                "names:\n  0: ulcer\n")

    print(f"seed {args.seed}; wound-size quartile edges (pixels): {[int(x) for x in q]}")
    print("split   images  ulcers  multi-ulcer imgs  median px  p10 px   p90 px")
    for s, ids in splits.items():
        a = np.array([info[i][1] for i in ids])
        u = np.array([info[i][0] for i in ids])
        print(f"{s:<6} {len(ids):>6} {u.sum():>7} {int((u > 1).sum()):>11} {int(np.median(a)):>14} {int(np.percentile(a, 10)):>8} {int(np.percentile(a, 90)):>9}")


if __name__ == "__main__":
    main()
