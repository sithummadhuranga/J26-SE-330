"""Finds the reference sticker in a photo, gives the real-world scale and cuts the straightened 500 x 500 picture.

What it uses: the black frame (50 mm outer side by default, 5 mm wide) with its white inside and a black orientation square
in one inner corner (see marker_layout.json). Colours are not used to find it, so the area measurement does not depend on the
printed colours.

Steps
  1. Mark dark pixels (darker than half of the local average) on a shrunken copy of the photo.
  2. Take dark shapes that have a hole (the frame), turn the outer outline into four corners.
  3. Go back to the full-size photo and fit a straight line to each of the four edges, then intersect them: sub-pixel corners.
  4. Straighten the sticker and check it looks like the sticker (dark frame, bright inside, ONE dark inner corner). The dark inner
     corner also tells which way up it is, so the four corners come out in the order they are printed.
  5. Build the homography from the sticker plane (mm) to photo pixels, the scale at the sticker, and the 500 x 500 picture.

Limits: the sticker must be flat, in the same plane as the wound, and all four corners must be visible and in focus.

    python sticker_finder.py <photo.jpg> [--side-mm 50] [--debug out.png]
"""
import argparse
import json
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

LAYOUT = json.loads(Path(__file__).with_name("marker_layout.json").read_text())
SIDE_MM = float(LAYOUT["side_mm"])
CROP = int(LAYOUT["crop"]["width"])  # the straightened picture is CROP x CROP for the whole printed sticker
SQ = LAYOUT["orientation_square"]
FRAME_MM = float(LAYOUT["frame_mm"])


@dataclass
class Sticker:
    corners: np.ndarray        # 4 x 2, photo pixels, order top-left, top-right, bottom-right, bottom-left of the printed sticker
    h_mm_to_px: np.ndarray     # 3 x 3, sticker plane in mm (origin top-left corner of the frame) to photo pixels
    px_per_mm: float           # photo pixels per mm at the sticker centre
    crop: np.ndarray           # CROP x CROP x 3 uint8 RGB, the straightened sticker
    orientation_contrast: float  # 0 to 1: how much darker the orientation corner is than the darkest of the other three, as a share of the black-to-white range
    frame_contrast: float      # 0 to 1: how much darker the frame is than the white inside


def _dark_mask(gray):
    k = max(15, (min(gray.shape) // 8) | 1)
    local = cv2.blur(gray.astype(np.float32), (k, k))
    dark = (gray.astype(np.float32) < 0.5 * local) & (gray < 140)
    dark = cv2.morphologyEx(dark.astype(np.uint8) * 255, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    return dark


def _quad(contour, min_side):
    hull = cv2.convexHull(contour)
    peri = cv2.arcLength(hull, True)
    for eps in np.linspace(0.01, 0.08, 15):
        approx = cv2.approxPolyDP(hull, eps * peri, True)
        if len(approx) == 4:
            q = approx.reshape(4, 2).astype(np.float64)
            sides = np.linalg.norm(q - np.roll(q, -1, axis=0), axis=1)
            if sides.min() >= min_side and sides.max() <= 4 * sides.min():
                return q
            return None
        if len(approx) < 4:
            return None
    return None


def _order_clockwise(q):
    c = q.mean(axis=0)
    ang = np.arctan2(q[:, 1] - c[1], q[:, 0] - c[0])
    q = q[np.argsort(ang)]            # increasing angle in image coordinates = clockwise on screen
    start = np.argmin(q.sum(axis=1))  # start at the corner nearest the top-left
    return np.roll(q, -start, axis=0)


def _refine(gray_full, q):
    """Fit a line to each edge of the frame's outer outline on the full-size photo and intersect them."""
    x0, y0 = np.floor(q.min(axis=0) - 0.15 * np.ptp(q, axis=0).max()).astype(int)
    x1, y1 = np.ceil(q.max(axis=0) + 0.15 * np.ptp(q, axis=0).max()).astype(int)
    x0, y0, x1, y1 = max(x0, 0), max(y0, 0), min(x1, gray_full.shape[1]), min(y1, gray_full.shape[0])
    roi = gray_full[y0:y1, x0:x1]
    dark = _dark_mask(roi)
    cs, hier = cv2.findContours(dark, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
    if hier is None:
        return q
    best, best_area = None, 0
    for i, c in enumerate(cs):
        if hier[0][i][3] == -1 and hier[0][i][2] != -1 and cv2.contourArea(c) > best_area:
            best, best_area = c, cv2.contourArea(c)
    if best is None:
        return q
    pts = best.reshape(-1, 2).astype(np.float64) + [x0, y0]
    lines = []
    for i in range(4):
        a, b = q[i], q[(i + 1) % 4]
        d = b - a
        n = np.linalg.norm(d)
        t = ((pts - a) @ d) / (n * n)
        dist = np.abs((pts - a) @ np.array([-d[1], d[0]]) / n)
        sel = (t > 0.12) & (t < 0.88) & (dist < max(3.0, 0.02 * n))
        if sel.sum() < 20:
            return q
        vx, vy, px, py = cv2.fitLine(pts[sel].astype(np.float32), cv2.DIST_HUBER, 0, 0.01, 0.01).ravel()
        lines.append((np.array([px, py], dtype=np.float64), np.array([vx, vy], dtype=np.float64)))
    out = []
    for i in range(4):
        (p1, d1), (p2, d2) = lines[i - 1], lines[i]
        A = np.array([d1, -d2]).T
        if abs(np.linalg.det(A)) < 1e-6:
            return q
        s = np.linalg.solve(A, p2 - p1)
        out.append(p1 + s[0] * d1)
    return np.array(out)


def _regions(img):
    """Mean brightness of the parts of a straightened 100 x 100 picture of the sticker (2 px per mm)."""
    s = img.shape[0] / SIDE_MM

    def m(x0, x1, y0, y1):
        return float(img[int(y0 * s):int(y1 * s), int(x0 * s):int(x1 * s)].mean())

    sq0, sq1 = SQ["x"], SQ["y"]
    far = SIDE_MM - FRAME_MM
    orient = m(sq0[0], sq0[1], sq1[0], sq1[1])
    others = [m(far - (sq0[1] - sq0[0]), far, sq1[0], sq1[1]),
              m(far - (sq0[1] - sq0[0]), far, far - (sq1[1] - sq1[0]), far),
              m(sq0[0], sq0[1], far - (sq1[1] - sq1[0]), far)]
    frame = np.mean([m(0.5, SIDE_MM - 0.5, 0.5, FRAME_MM - 1), m(0.5, SIDE_MM - 0.5, SIDE_MM - FRAME_MM + 1, SIDE_MM - 0.5),
                     m(0.5, FRAME_MM - 1, 0.5, SIDE_MM - 0.5), m(SIDE_MM - FRAME_MM + 1, SIDE_MM - 0.5, 0.5, SIDE_MM - 0.5)])
    white = m(*LAYOUT["white_reference"]["x"], *LAYOUT["white_reference"]["y"])
    return orient, others, frame, white


def _orient(gray_blur, q):
    """Try the four ways round; return the corner order whose orientation square is dark and the other three corners are not."""
    W = 100
    dst = np.array([[0, 0], [W, 0], [W, W], [0, W]], dtype=np.float32)
    best = None
    for k in range(4):
        qk = np.roll(q, -k, axis=0).astype(np.float32)
        canon = cv2.warpPerspective(gray_blur, cv2.getPerspectiveTransform(qk, dst), (W, W), flags=cv2.INTER_AREA)
        orient, others, frame, white = _regions(canon.astype(np.float32))
        span = max(white - frame, 1.0)             # the black-to-white range of this photo
        oc = (min(others) - orient) / span         # how much darker the orientation corner is than the darkest other corner
        fc = (white - frame) / (white + 1.0)       # how much darker the frame is than the white inside
        if best is None or oc > best[0]:
            best = (oc, fc, k)
    return best


def find_stickers(rgb, side_mm=SIDE_MM, max_dim=1600, min_orient=0.5, min_frame=0.5):
    """All stickers found in an RGB photo (uint8), largest first. Empty list when none is found."""
    h, w = rgb.shape[:2]
    s = min(1.0, max_dim / max(h, w))
    gray_full = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    small = cv2.resize(gray_full, None, fx=s, fy=s, interpolation=cv2.INTER_AREA) if s < 1 else gray_full
    dark = _dark_mask(cv2.GaussianBlur(small, (3, 3), 0))
    cs, hier = cv2.findContours(dark, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
    out = []
    if hier is None:
        return out
    min_side = 0.04 * max(small.shape)
    for i, c in enumerate(cs):
        if hier[0][i][3] != -1 or hier[0][i][2] == -1 or cv2.contourArea(c) < min_side ** 2:
            continue
        q = _quad(c, min_side)
        if q is None:
            continue
        q = _order_clockwise(q / s)
        q = _order_clockwise(_refine(gray_full, q))
        blur = cv2.GaussianBlur(gray_full, (0, 0), max(0.5, 0.6 * np.linalg.norm(q[0] - q[1]) / 100))
        oc, fc, k = _orient(blur, q)
        if oc < min_orient or fc < min_frame:
            continue
        q = np.roll(q, -k, axis=0)
        mm = np.array([[0, 0], [side_mm, 0], [side_mm, side_mm], [0, side_mm]], dtype=np.float32)
        H = cv2.getPerspectiveTransform(mm, q.astype(np.float32)).astype(np.float64)
        c0 = np.array([[[side_mm / 2, side_mm / 2], [side_mm / 2 + 1, side_mm / 2], [side_mm / 2, side_mm / 2 + 1]]], dtype=np.float32)
        p = cv2.perspectiveTransform(c0, H)[0]
        ppm = float(np.sqrt(abs(np.linalg.det(np.array([p[1] - p[0], p[2] - p[0]]).T))))
        dstc = np.array([[0, 0], [CROP, 0], [CROP, CROP], [0, CROP]], dtype=np.float32)
        f = min(1.0, (CROP / side_mm) / ppm)  # shrink first if the photo has more pixels per mm than the picture
        src = cv2.resize(rgb, None, fx=f, fy=f, interpolation=cv2.INTER_AREA) if f < 0.95 else rgb
        f = f if f < 0.95 else 1.0
        crop = cv2.warpPerspective(src, cv2.getPerspectiveTransform((q * f).astype(np.float32), dstc), (CROP, CROP), flags=cv2.INTER_LINEAR)
        out.append(Sticker(q, H, ppm, crop, float(oc), float(fc)))
    return sorted(out, key=lambda t: -t.px_per_mm)


def area_mm2(mask, sticker, res=0.1):
    """Area of a mask in photo pixels, in mm2, using the homography of the sticker (corrects for camera tilt)."""
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return 0.0
    Hi = np.linalg.inv(sticker.h_mm_to_px)
    box = np.array([[[xs.min(), ys.min()], [xs.max() + 1, ys.min()], [xs.max() + 1, ys.max() + 1], [xs.min(), ys.max() + 1]]], dtype=np.float32)
    mm = cv2.perspectiveTransform(box, Hi)[0]
    x0, y0 = mm.min(axis=0) - 1
    x1, y1 = mm.max(axis=0) + 1
    size = (int(np.ceil((x1 - x0) / res)), int(np.ceil((y1 - y0) / res)))
    T = np.array([[res, 0, x0], [0, res, y0], [0, 0, 1]])
    warped = cv2.warpPerspective(mask.astype(np.float32), sticker.h_mm_to_px @ T, size, flags=cv2.INTER_LINEAR | cv2.WARP_INVERSE_MAP)
    return float(warped.sum() * res * res)


def area_uniform_mm2(mask, sticker):
    """The same area with one scale for the whole picture (what a ruler can give): pixels / (pixels per mm)^2."""
    return float(mask.sum() / sticker.px_per_mm ** 2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("photo")
    ap.add_argument("--side-mm", type=float, default=SIDE_MM)
    ap.add_argument("--debug")
    a = ap.parse_args()
    rgb = cv2.cvtColor(cv2.imread(a.photo), cv2.COLOR_BGR2RGB)
    found = find_stickers(rgb, a.side_mm)
    print(f"{len(found)} sticker(s) found in a {rgb.shape[1]} x {rgb.shape[0]} photo")
    dbg = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
    for i, s in enumerate(found):
        print(f"  #{i}: {s.px_per_mm:.2f} px/mm at the centre, orientation contrast {s.orientation_contrast:.2f}, frame contrast {s.frame_contrast:.2f}")
        cv2.polylines(dbg, [s.corners.astype(np.int32)], True, (0, 255, 0), max(2, rgb.shape[1] // 600))
        cv2.putText(dbg, str(i), tuple(s.corners[0].astype(int)), cv2.FONT_HERSHEY_SIMPLEX, rgb.shape[1] / 1500, (0, 0, 255), 3)
    if a.debug:
        cv2.imwrite(a.debug, dbg)


if __name__ == "__main__":
    main()
