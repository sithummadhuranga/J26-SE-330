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


def _linear(rgb):
    x = rgb.astype(np.float32) / 255.0
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def shape_alpha(rgb, contour):
    """How much of each pixel near the shape is shape (0 to 1), not just in or out. A hard colour threshold puts the edge at a
    level that moves with blur, tilt and print; summing the share of every edge pixel does not (blur moves the share around
    but keeps the sum). The paper level is taken from a ring outside the shape and the fill level from its inside; the maths is done
    in linear light, where blending two colours is a straight mix. Falls back to the hard shape when fill and paper look alike."""
    hard = np.zeros(rgb.shape[:2], np.uint8)
    cv2.drawContours(hard, [contour], -1, 1, -1)
    k = int(max(8, 0.04 * np.sqrt(hard.sum())))      # a margin wider than the blur of the photo
    inner = cv2.erode(hard, np.ones((2 * k + 1, 2 * k + 1), np.uint8))
    ring = cv2.dilate(hard, np.ones((6 * k + 1, 6 * k + 1), np.uint8)) - cv2.dilate(hard, np.ones((4 * k + 1, 4 * k + 1), np.uint8))
    lin = _linear(rgb)
    red = lin[..., 0] - 0.5 * (lin[..., 1] + lin[..., 2])
    if inner.sum() == 0 or ring.sum() == 0:
        return hard.astype(np.float32)
    fill, paper = float(np.median(red[inner > 0])), float(np.median(red[ring > 0]))
    if fill - paper < 0.1:
        return hard.astype(np.float32)
    near = cv2.dilate(hard, np.ones((2 * k + 1, 2 * k + 1), np.uint8))
    return (np.clip((red - paper) / (fill - paper), 0, 1) * near).astype(np.float32)


def measure_photo(rgb, side_mm, truth=None):
    """Stickers found in an RGB photo and, for each red shape, its area with the homography (hard mask: hom, and soft coverage:
    soft, the better one) and with one scale (uni). Returns (stickers, rows); each row: kind, printed, hom, soft, uni (mm2),
    hom_pct, soft_pct, uni_pct (errors in %), contour. truth: printed area per shape (default: the first sheet's)."""
    truth = truth or TRUTH
    stickers = SF.find_stickers(rgb, side_mm)
    rows = []
    if not stickers:
        return stickers, rows
    m = red_shapes(rgb)
    cs, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    for c in sorted(cs, key=cv2.contourArea, reverse=True)[:3]:
        kind = classify(c)
        if kind is None or cv2.contourArea(c) < 2000:
            continue
        mask = np.zeros(m.shape, np.uint8)
        cv2.drawContours(mask, [c], -1, 1, -1)
        ctr = np.array(c.reshape(-1, 2).mean(axis=0))
        st = min(stickers, key=lambda t: np.linalg.norm(t.corners.mean(axis=0) - ctr))
        hom, uni = SF.area_mm2(mask, st), SF.area_uniform_mm2(mask, st)
        soft = SF.area_mm2(shape_alpha(rgb, c), st)
        rows.append(dict(kind=kind, printed=truth[kind], hom=hom, soft=soft, uni=uni, hom_pct=100 * (hom - truth[kind]) / truth[kind],
                         soft_pct=100 * (soft - truth[kind]) / truth[kind], uni_pct=100 * (uni - truth[kind]) / truth[kind], contour=c))
    return stickers, rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("photos", nargs="+")
    ap.add_argument("--side-mm", type=float, default=SF.SIDE_MM)
    ap.add_argument("--sheet", type=int, choices=(1, 2), default=1, help="which printed sheet: 1 (first) or 2 (shapes between two rows of stickers)")
    ap.add_argument("--debug-dir")
    a = ap.parse_args()
    all_soft, all_uni = [], []
    truth = TRUTH
    if a.sheet == 2:
        import make_sticker_sheet
        truth = make_sticker_sheet.build_sheet_v2()[1]["areas"]
    for path in a.photos:
        bgr = cv2.imread(path)
        if bgr is None:
            print(path, ": cannot read"); continue
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        stickers, rows = measure_photo(rgb, a.side_mm, truth)
        print(f"\n{Path(path).name}: {rgb.shape[1]} x {rgb.shape[0]}, {len(stickers)} sticker(s) found" + (
            f", {min(s.px_per_mm for s in stickers):.1f}-{max(s.px_per_mm for s in stickers):.1f} px/mm" if stickers else ""))
        dbg = bgr.copy()
        for s_ in stickers:
            cv2.polylines(dbg, [s_.corners.astype(np.int32)], True, (0, 255, 0), max(2, rgb.shape[1] // 600))
        for r in rows:
            all_soft.append(abs(r["soft_pct"])); all_uni.append(abs(r["uni_pct"]))
            print(f"  {r['kind']:9s} printed {r['printed']:7.1f} mm2 | edge coverage {r['soft']:7.1f} ({r['soft_pct']:+.1f}%) | hard threshold {r['hom']:7.1f} ({r['hom_pct']:+.1f}%) | one scale {r['uni']:7.1f} ({r['uni_pct']:+.1f}%)")
            cv2.drawContours(dbg, [r["contour"]], -1, (255, 0, 0), max(2, rgb.shape[1] // 800))
        if a.debug_dir:
            Path(a.debug_dir).mkdir(parents=True, exist_ok=True)
            cv2.imwrite(str(Path(a.debug_dir) / (Path(path).stem + "_debug.jpg")), dbg)
    if all_soft:
        print(f"\nAll shapes: |error| median {np.median(all_soft):.2f}% (max {max(all_soft):.2f}%) with edge coverage and the sticker, "
              f"{np.median(all_uni):.2f}% (max {max(all_uni):.2f}%) with one scale; {len(all_soft)} measurements")


if __name__ == "__main__":
    main()
