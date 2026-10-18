"""K-Means colour regions of a wound: how the wound's pixels split into a few colour groups, as percentages.

This describes colour only. It does not say what tissue a group is (NFR5 of the proposal: no tissue labels).

The method is deterministic, so Python and the phone (mobile/lib/features/measurement/colour_regions.dart) give the same
answer for the same pixels, which a randomly started K-Means would not:
  1. take the wound's RGB pixels; if there are more than max_sample, keep every n-th one, in row order
  2. first centre: the sample pixel closest to the mean colour; each next centre: the pixel farthest from the centres so far
  3. Lloyd iterations (assign to the nearest centre, move centres to the group mean) until nothing changes
  4. assign every wound pixel to a centre; groups are numbered 1.. from largest to smallest
Percentages are rounded to one decimal and always add up to 100.0.
k = 3 was chosen on the 202 validation wounds: the best fit without tiny groups (k=5 gives a group under 3% on 11 of 199 wounds).

    python colour_regions.py --golden <golden dir>            check the files written by litert_reference.py
    python colour_regions.py --golden <golden dir> --write    also write ID.regions.json for the Dart test
"""
import argparse
import json
import os

import numpy as np

K = 3
MAX_SAMPLE = 4000
MAX_ITER = 30


def colour_regions(rgb, mask, k=K, max_sample=MAX_SAMPLE, max_iter=MAX_ITER):
    """rgb: (H, W, 3) uint8, mask: (H, W) 0/1. Returns [{"cluster": 1.., "percent": float, "centre": [r, g, b]}] or []."""
    pix = rgb[mask.astype(bool)].astype(np.float64)
    n = len(pix)
    if n == 0:
        return []
    step = -(-n // max_sample)
    sample = pix[::step]

    centres = [sample[np.argmin(np.linalg.norm(sample - sample.mean(axis=0), axis=1))]]
    dist = np.linalg.norm(sample - centres[0], axis=1)
    while len(centres) < min(k, len(sample)):
        far = int(np.argmax(dist))
        if dist[far] == 0:  # fewer distinct colours than k
            break
        centres.append(sample[far])
        dist = np.minimum(dist, np.linalg.norm(sample - centres[-1], axis=1))
    centres = np.array(centres)

    labels = None
    for _ in range(max_iter):
        new = np.argmin(np.linalg.norm(sample[:, None, :] - centres[None, :, :], axis=2), axis=1)
        if labels is not None and np.array_equal(new, labels):
            break
        labels = new
        for c in range(len(centres)):
            if (labels == c).any():
                centres[c] = sample[labels == c].mean(axis=0)

    full = np.argmin(np.linalg.norm(pix[:, None, :] - centres[None, :, :], axis=2), axis=1)
    counts = np.bincount(full, minlength=len(centres))
    order = [c for c in np.argsort(-counts, kind="stable") if counts[c] > 0]
    pct = _half_up(counts[order] / n * 1000, 10)
    pct[0] = _half_up((pct[0] + (100.0 - pct.sum())) * 10, 10)  # rounding leftovers go to the largest group
    return [{"cluster": i + 1, "percent": float(pct[i]), "centre": [float(_half_up(v * 100, 100)) for v in centres[order[i]]]}
            for i in range(len(order))]


def _half_up(x, scale):
    """Round x/scale-style values half up (x already multiplied by scale/10 or scale); Dart does the same."""
    return np.floor(np.asarray(x, dtype=np.float64) + 0.5) / scale


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--golden", required=True)
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()
    manifest = json.load(open(os.path.join(args.golden, "manifest.json")))
    h, w = manifest["input_hw"]
    for img in manifest["images"]:
        i = img["id"]
        rgb = np.fromfile(os.path.join(args.golden, i + ".rgb.u8"), np.uint8).reshape(h, w, 3)
        mask = np.fromfile(os.path.join(args.golden, i + ".union.u8"), np.uint8).reshape(h, w)
        regions = colour_regions(rgb, mask)
        assert regions == colour_regions(rgb, mask), "not deterministic"
        assert not regions or abs(sum(r["percent"] for r in regions) - 100.0) < 1e-6
        print(i, f"{int(mask.sum()):6d} px ->", [(r["cluster"], r["percent"]) for r in regions] or "no wound")
        if args.write:
            json.dump(regions, open(os.path.join(args.golden, i + ".regions.json"), "w"))


if __name__ == "__main__":
    main()
