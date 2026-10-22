"""Build the Fitzpatrick17k 64x64 skin-patch dataset (T50).

Reads the download log, splits IMAGES (never patches) into train/val/test stratified by
Fitzpatrick type, then cuts up to --per-image skin patches from each image. A patch is kept
only if at least --min-skin of its pixels pass the HSV skin mask. Patches are saved as PNG
and listed in train.csv / val.csv / test.csv; split_images.csv records the image split.
A class-balance table is printed (the input for T51).

Licence: CC BY-NC-SA 3.0, scientific use only. Write --out outside the repo.

Usage (from research/IT23343702/colorimetric-calibration):
    .venv/bin/python scripts/build_patch_dataset.py \
        --data ~/Documents/Color-pipeline/data/fitzpatrick17k \
        --out ~/Documents/Color-pipeline/data/fst_patches
"""

from __future__ import annotations

import argparse
import csv
import sys
from collections import Counter, defaultdict
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.ita import skin_mask_hsv  # noqa: E402

PATCH = 64
SPLITS = ("train", "val", "test")


def split_images(rows: list[dict], fractions: tuple[float, float, float], rng: np.random.Generator) -> dict[str, str]:
    """Stratified image-level split: {md5hash: split}."""
    by_fst: dict[str, list[str]] = defaultdict(list)
    for r in rows:
        by_fst[r["fitzpatrick_scale"]].append(r["md5hash"])
    out: dict[str, str] = {}
    for hashes in by_fst.values():
        hashes = sorted(hashes)
        rng.shuffle(hashes)
        n_val, n_test = round(len(hashes) * fractions[1]), round(len(hashes) * fractions[2])
        for i, h in enumerate(hashes):
            out[h] = "val" if i < n_val else "test" if i < n_val + n_test else "train"
    return out


def cut_patches(rgb: np.ndarray, per_image: int, min_skin: float, rng: np.random.Generator) -> list[np.ndarray]:
    h, w = rgb.shape[:2]
    if min(h, w) < PATCH:
        return []
    mask = skin_mask_hsv(rgb).astype(np.float32)
    # fraction of skin pixels in every 64x64 window, via box filter
    cover = cv2.boxFilter(mask, -1, (PATCH, PATCH), normalize=True, borderType=cv2.BORDER_CONSTANT)
    ys, xs = np.nonzero(cover[PATCH // 2 : h - PATCH // 2 + 1, PATCH // 2 : w - PATCH // 2 + 1] >= min_skin)
    if len(ys) == 0:
        return []
    picks: list[tuple[int, int]] = []
    for i in rng.permutation(len(ys)):
        y, x = int(ys[i]), int(xs[i])
        # keep patches apart so they do not overlap by more than half
        if all(abs(y - py) >= PATCH // 2 or abs(x - px) >= PATCH // 2 for py, px in picks):
            picks.append((y, x))
            if len(picks) == per_image:
                break
    return [rgb[y : y + PATCH, x : x + PATCH] for y, x in picks]


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", type=Path, default=Path("data/fitzpatrick17k"))
    p.add_argument("--out", type=Path, default=Path("data/fst_patches"))
    p.add_argument("--per-image", type=int, default=8)
    p.add_argument("--min-skin", type=float, default=0.8)
    p.add_argument("--seed", type=int, default=0)
    args = p.parse_args()
    data, out = args.data.expanduser(), args.out.expanduser()
    rng = np.random.default_rng(args.seed)

    with (data / "download_log.csv").open(newline="") as f:
        rows = [r for r in csv.DictReader(f) if r["status"] == "ok" and (data / "images" / f"{r['md5hash']}.jpg").exists()]
    split = split_images(rows, (0.70, 0.15, 0.15), rng)

    out.mkdir(parents=True, exist_ok=True)
    for s in SPLITS:
        (out / s).mkdir(exist_ok=True)
    lists: dict[str, list[list]] = {s: [] for s in SPLITS}
    no_patch = 0
    for r in rows:
        bgr = cv2.imread(str(data / "images" / f"{r['md5hash']}.jpg"))
        if bgr is None:
            continue
        patches = cut_patches(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB), args.per_image, args.min_skin, rng)
        no_patch += not patches
        s = split[r["md5hash"]]
        for i, patch in enumerate(patches):
            name = f"{s}/{r['md5hash']}_{i}.png"
            cv2.imwrite(str(out / name), cv2.cvtColor(patch, cv2.COLOR_RGB2BGR))
            lists[s].append([name, r["md5hash"], r["fitzpatrick_scale"]])

    for s in SPLITS:
        with (out / f"{s}.csv").open("w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["patch", "image", "fst"])
            w.writerows(lists[s])
    with (out / "split_images.csv").open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["image", "fst", "split"])
        w.writerows([r["md5hash"], r["fitzpatrick_scale"], split[r["md5hash"]]] for r in rows)

    leak = {s: {x[1] for x in lists[s]} for s in SPLITS}
    assert not (leak["train"] & leak["val"] or leak["train"] & leak["test"] or leak["val"] & leak["test"]), "image in two splits"
    print(f"{len(rows)} images, {no_patch} gave no patch\n")
    print("FST  " + "  ".join(f"{s:>12}" for s in SPLITS))
    for k in "123456":
        cells = []
        for s in SPLITS:
            n_p = sum(1 for x in lists[s] if x[2] == k)
            n_i = len({x[1] for x in lists[s] if x[2] == k})
            cells.append(f"{n_p:>6}p/{n_i:>3}i")
        print(f"{k:>3}  " + "  ".join(cells))
    print("\npatches:", {s: len(lists[s]) for s in SPLITS}, "| FST share (train):",
          {k: round(v / max(len(lists["train"]), 1), 3) for k, v in sorted(Counter(x[2] for x in lists["train"]).items())})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
