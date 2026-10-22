"""Measures the area of the three red shapes on a real photo of the printed sheet and compares with the areas printed under them.

For each photo: find the stickers (sticker_finder.py), cut out the red shapes, and compute each shape's area two ways: with the
homography of the nearest sticker (corrects for camera tilt) and with one scale for the whole photo (what a ruler would give).
The printed areas are counted from the print file at 100% size; the printer adds about 1% tolerance of its own.

    python area_real_photo.py <photo> [<photo> ...] --side-mm 50.0 [--debug-dir <folder>]

Give the REAL side of a cut sticker, measured with a ruler, as --side-mm (default 50). The sheet must be printed at 100%.
"""
import argparse
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sticker_finder as SF  # noqa: E402

TRUTH = {"rectangle": 999.2, "circle": 706.4, "triangle": 602.5}  # mm2, counted from the print file


def red_shapes(rgb):
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    h, s, v = hsv[..., 0], hsv[..., 1], hsv[..., 2]
    m = (((h <= 10) | (h >= 170)) & (s > 110) & (v > 50)).astype(np.uint8)
    return cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))


def classify(contour):
    peri = cv2.arcLength(contour, True)
    n = len(cv2.approxPolyDP(contour, 0.03 * peri, True))
    return "triangle" if n == 3 else "rectangle" if n == 4 else "circle" if n >= 8 else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("photos", nargs="+")
    ap.add_argument("--side-mm", type=float, default=SF.SIDE_MM)
    ap.add_argument("--debug-dir")
    a = ap.parse_args()
    all_hom, all_uni = [], []
    for path in a.photos:
        bgr = cv2.imread(path)
        if bgr is None:
            print(path, ": cannot read"); continue
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        stickers = SF.find_stickers(rgb, a.side_mm)
        print(f"\n{Path(path).name}: {rgb.shape[1]} x {rgb.shape[0]}, {len(stickers)} sticker(s) found" + (
            f", {min(s.px_per_mm for s in stickers):.1f}-{max(s.px_per_mm for s in stickers):.1f} px/mm" if stickers else ""))
        if not stickers:
            continue
        m = red_shapes(rgb)
        cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        dbg = bgr.copy()
        for s in stickers:
            cv2.polylines(dbg, [s.corners.astype(np.int32)], True, (0, 255, 0), max(2, rgb.shape[1] // 600))
        for c in sorted(cs, key=cv2.contourArea, reverse=True)[:3]:
            kind = classify(c)
            if kind is None or cv2.contourArea(c) < 2000:
                continue
            mask = np.zeros(m.shape, np.uint8)
            cv2.drawContours(mask, [c], -1, 1, -1)
            ctr = np.array(c.reshape(-1, 2).mean(axis=0))
            st = min(stickers, key=lambda t: np.linalg.norm(t.corners.mean(axis=0) - ctr))
            hom, uni = SF.area_mm2(mask, st), SF.area_uniform_mm2(mask, st)
            eh, eu = 100 * (hom - TRUTH[kind]) / TRUTH[kind], 100 * (uni - TRUTH[kind]) / TRUTH[kind]
            all_hom.append(abs(eh)); all_uni.append(abs(eu))
            print(f"  {kind:9s} printed {TRUTH[kind]:7.1f} mm2 | homography {hom:7.1f} ({eh:+.1f}%) | one scale {uni:7.1f} ({eu:+.1f}%)")
            cv2.drawContours(dbg, [c], -1, (255, 0, 0), max(2, rgb.shape[1] // 800))
        if a.debug_dir:
            Path(a.debug_dir).mkdir(parents=True, exist_ok=True)
            cv2.imwrite(str(Path(a.debug_dir) / (Path(path).stem + "_debug.jpg")), dbg)
    if all_hom:
        print(f"\nAll shapes: |error| median {np.median(all_hom):.2f}% (max {max(all_hom):.2f}%) with the homography, "
              f"{np.median(all_uni):.2f}% (max {max(all_uni):.2f}%) with one scale; {len(all_hom)} measurements")


if __name__ == "__main__":
    main()
