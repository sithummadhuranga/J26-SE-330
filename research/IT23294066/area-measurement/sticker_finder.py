"""Finds the reference sticker in a photo, gives the real-world scale and cuts the straightened 500 x 500 picture.

What it uses: the black frame (50 mm outer side by default, 5 mm wide) with its white inside and a black orientation square
in one inner corner (see marker_layout.json). Colours are not used to find it, so the area measurement does not depend on the
printed colours.

Steps
  1. Mark dark pixels (darker than half of the local average) on a shrunken copy of the photo.
  2. Take dark shapes that have a hole (the frame), turn the outer outline into four corners.
  3. Go back to the full-size photo and fit a straight line to each of the four edges, then intersect them: sub-pixel corners.
     The edge sits where the light is half way between the black frame and the paper outside (not at a darkness threshold,
     which makes a blurred sticker look smaller and every area about 1% too large).
  4. Straighten the sticker and check it looks like the sticker (dark frame, bright inside, ONE dark inner corner, the patches where
     the layout puts them). The dark inner corner also tells which way up it is, so the four corners come out in the order they
     are printed. A mirrored sticker (a front camera, a mirrored preview) is rejected: its patches would be in the wrong places.
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
GAMMA = 2.2  # the edge level is found in linear light: blur mixes light, so the geometric edge is half the light, not half the display value


@dataclass
class Sticker:
    corners: np.ndarray        # 4 x 2, photo pixels, order top-left, top-right, bottom-right, bottom-left of the printed sticker
    h_mm_to_px: np.ndarray     # 3 x 3, sticker plane in mm (origin top-left corner of the frame) to photo pixels
    px_per_mm: float           # photo pixels per mm at the sticker centre
    crop: np.ndarray           # CROP x CROP x 3 uint8 RGB, the straightened sticker
    orientation_contrast: float  # 0 to 1: how much darker the orientation corner is than the darkest of the other three, as a share of the black-to-white range
    frame_contrast: float      # 0 to 1: how much darker the frame is than the white inside


def _dark_mask(gray, factor=0.5, abs_max=140):
    k = max(15, (min(gray.shape) // 8) | 1)
    local = cv2.blur(gray.astype(np.float32), (k, k))
    dark = (gray.astype(np.float32) < factor * local) & (gray < abs_max)
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
    out = _intersect(lines)
    return q if out is None else _subpixel_corners(gray_full, out)


def _intersect(lines):
    """Corners from four (point, direction) lines in order; None when two neighbours are parallel."""
    out = []
    for i in range(4):
        (p1, d1), (p2, d2) = lines[i - 1], lines[i]
        A = np.array([d1, -d2]).T
        if abs(np.linalg.det(A)) < 1e-6:
            return None
        s = np.linalg.solve(A, p2 - p1)
        out.append(p1 + s[0] * d1)
    return np.array(out)


def _subpixel_corners(gray, q, frame_px=None, n_profiles=41, step=0.25, gamma=GAMMA):
    """Moves the four corners to where the frame's outer edge really is: the half-way level between the black frame and what is
    outside it, found on brightness profiles across each edge. A darkness threshold sits inside that point on a blurred photo, so
    it makes the sticker look smaller and every area too large (about 1% at the blur of a phone photo). Returns q when an edge has
    too little contrast or the fit moves more than a tenth of a side (then the first estimate stays)."""
    g = (gray.astype(np.float32) / 255.0) ** gamma * 255.0 if gamma != 1.0 else gray.astype(np.float32)
    c = q.mean(axis=0)
    lines = []
    for i in range(4):
        a, b = q[i], q[(i + 1) % 4]
        L = np.linalg.norm(b - a)
        d = (b - a) / L
        nrm = np.array([-d[1], d[0]])
        if np.dot(nrm, (a + b) / 2 - c) < 0:
            nrm = -nrm                                                           # pointing away from the sticker
        f = frame_px if frame_px is not None else L * FRAME_MM / SIDE_MM         # the frame's width in pixels
        s = np.arange(-0.8 * f, 0.8 * f, step)
        t = np.linspace(0.15, 0.85, n_profiles)
        pts = a[None, :] + t[:, None] * (b - a)[None, :]
        mx = (pts[:, 0:1] + s[None, :] * nrm[0]).astype(np.float32)
        my = (pts[:, 1:2] + s[None, :] * nrm[1]).astype(np.float32)
        prof = cv2.remap(g, mx, my, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
        prof = cv2.blur(prof, (5, 1))
        inside = np.median(prof[:, (s > -0.7 * f) & (s < -0.3 * f)], axis=1)
        outside = np.median(prof[:, (s > 0.3 * f) & (s < 0.8 * f)], axis=1)
        near = np.abs(s) < 0.35 * f
        good = []
        for k in range(n_profiles):
            if outside[k] - inside[k] < 20:
                continue
            half = (inside[k] + outside[k]) / 2
            p = prof[k]
            idx = np.nonzero((p[:-1] < half) & (p[1:] >= half) & near[:-1])[0]
            if len(idx) == 0:
                continue
            j = idx[np.argmin(np.abs(s[idx]))]
            x = s[j] + step * (half - p[j]) / max(p[j + 1] - p[j], 1e-6)
            good.append(pts[k] + x * nrm)
        if len(good) < 15:
            return q
        vx, vy, px, py = cv2.fitLine(np.array(good, dtype=np.float32), cv2.DIST_HUBER, 0, 0.01, 0.01).ravel()
        lines.append((np.array([px, py], dtype=np.float64), np.array([vx, vy], dtype=np.float64)))
    out = _intersect(lines)
    if out is None or np.linalg.norm(out - q, axis=1).max() > 0.1 * np.linalg.norm(q[1] - q[0]):
        return q
    return out


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
            best = (oc, fc, k, canon.astype(np.float32))
    return best


def _layout_numbers(canon):
    """(brightest patch cell, darkest cell where a mirrored sticker would have a patch), both as a share of the white area."""
    s = canon.shape[0] / SIDE_MM

    def cell(cx, cy, size):
        half = 0.3 * size
        return float(canon[int((cy - half) * s):int((cy + half) * s), int((cx - half) * s):int((cx + half) * s)].mean())

    white = _regions(canon)[3] + 1.0
    patches = [cell(*p["centre"], p["size"]) for p in LAYOUT["patches"]]
    swapped = [cell(p["centre"][1], p["centre"][0], p["size"]) for p in LAYOUT["patches"] if p["centre"][0] != p["centre"][1]]
    return max(patches) / white, min(swapped) / white


def _layout_ok(canon, max_patch=0.9, min_empty=0.8):
    """The mid-tone patches must lie where marker_layout.json puts them, and the cells where a mirrored sticker would have them
    (the patch layout with x and y swapped) must be bare white. A mirrored sticker still has one dark corner, but its patches end up
    in a column instead of a row, so its straightened picture would be misread."""
    patch, empty = _layout_numbers(canon)
    return patch < max_patch and empty > min_empty


def _patches_washed(canon, max_patch=0.9, min_empty=0.8):
    """No patch can be seen anywhere, in the right cells or the mirrored ones: the patches are washed out (glare), not another sticker."""
    patch, empty = _layout_numbers(canon)
    return patch >= max_patch and empty >= min_empty


REJECT_CODES = ("no_candidate", "cut_by_edge", "weak_frame_contrast", "weak_orientation", "layout_mismatch", "mirrored")


@dataclass
class Diagnosis:
    stickers: list                    # the same list find_stickers gives
    code: object                      # None when at least one sticker was accepted, else one of REJECT_CODES
    best_frame_contrast: float        # the best of the candidates that were looked at closely (0 when there were none)
    best_orientation_contrast: float
    candidates: int                   # how many frame-like shapes were looked at closely


def _reject_code(oc, fc, layout, transposed, min_orient, min_frame, washed=False):
    """Why one frame-like shape was turned down, in a fixed order: a faint frame first (glare, shadow), then a missing or doubled
    orientation square, then the patch layout. If the layout only fits with x and y swapped, the sticker is mirrored (the fix for
    the nurse is different: use the rear camera). None when the shape is accepted."""
    if fc < min_frame:
        return "weak_frame_contrast"
    if oc < min_orient:
        return "weak_orientation"
    if not layout:
        if washed:
            return "weak_frame_contrast"                 # the frame is fine but the patches are washed out: glare over them
        return "mirrored" if transposed else "layout_mismatch"
    return None


def _touches_border_dark(dark, gray, min_side):
    """True when a dark shape touches the edge of the photo and has bright paper inside its bounding box: a frame that is cut off.
    A solid dark object at the edge (a bag, a chair, a shadow) has no bright paper inside, so it does not count."""
    cs, _ = cv2.findContours(dark, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    H, W = dark.shape[:2]
    paper = max(200.0, 0.85 * float(np.percentile(gray, 95)))      # paper is near white; skin and cloth are not
    for c in cs:
        if cv2.contourArea(c) < min_side ** 2:
            continue
        x, y, w, h = cv2.boundingRect(c)
        if not (x <= 1 or y <= 1 or x + w >= W - 1 or y + h >= H - 1):
            continue
        if float((gray[y:y + h, x:x + w] > paper).mean()) >= 0.25:
            return True
    return False


MIN_FRAME_SIGNAL = 0.08  # below this the shape has no brighter inside than its frame: it cannot be a sticker, glare or not
HOLE_SHARE = (0.5, 0.8)   # the white inside of the frame is 64% of the sticker's area (40 x 40 of 50 x 50; measured 0.60-0.66 on real photos)


def _hole_share_ok(cs, hier, i):
    """Only a shape whose biggest hole is about 64% of its whole area can be the sticker's frame. Shadows, creases and dark folds
    make hollow outlines too (thin rings with a hole of 90%+, thick blocks with a hole of a few percent), and they must not be
    blamed on a sticker problem: on 60 wound photos with no sticker the share of the false candidates was 0.00-0.39."""
    outer = cv2.contourArea(cs[i])
    best, k = 0.0, hier[0][i][2]
    while k != -1:
        best = max(best, cv2.contourArea(cs[k]))
        k = hier[0][k][0]
    return outer > 0 and HOLE_SHARE[0] <= best / outer <= HOLE_SHARE[1]


def _scan(rgb, side_mm, max_dim, min_orient, min_frame, lenient=False):
    """Everything find_stickers does, plus a note for every frame-like shape that was turned down: (area, code, oc, fc).
    lenient=True looks for frames that are only a little darker than their surroundings (glare lifts the blacks); it is used only
    to explain a failure, and a shape found that way is never accepted as a sticker."""
    h, w = rgb.shape[:2]
    s = min(1.0, max_dim / max(h, w))
    gray_full = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    small = cv2.resize(gray_full, None, fx=s, fy=s, interpolation=cv2.INTER_AREA) if s < 1 else gray_full
    dark = _dark_mask(cv2.GaussianBlur(small, (3, 3), 0), *((0.8, 215) if lenient else (0.5, 140)))
    cs, hier = cv2.findContours(dark, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_NONE)
    out, notes = [], []
    min_side = 0.04 * max(small.shape)
    if hier is None:
        return out, notes, small, dark, min_side
    for i, c in enumerate(cs):
        if hier[0][i][3] != -1 or hier[0][i][2] == -1 or cv2.contourArea(c) < min_side ** 2:
            continue
        if not _hole_share_ok(cs, hier, i):
            continue                                       # a dark outline, but not shaped like the sticker's frame
        q = _quad(c, min_side)
        if q is None:
            continue
        q = _order_clockwise(q / s)
        q = _order_clockwise(_refine(gray_full, q))
        blur = cv2.GaussianBlur(gray_full, (0, 0), max(0.5, 0.6 * np.linalg.norm(q[0] - q[1]) / 100))
        oc, fc, k, canon = _orient(blur, q)
        if fc < MIN_FRAME_SIGNAL:
            continue                                       # no bright inside at all: a dark block or shadow, not a faded sticker
        layout = _layout_ok(canon)
        code = _reject_code(oc, fc, layout, False if layout else _layout_ok(canon.T), min_orient, min_frame,
                            washed=False if layout else _patches_washed(canon))
        if lenient and code is None:
            code = "weak_frame_contrast"                      # it passes only because the mask was loosened: the frame is not dark
        if code is not None:
            notes.append((float(cv2.contourArea(c)), code, float(oc), float(fc)))
            continue
        notes.append((float(cv2.contourArea(c)), None, float(oc), float(fc)))
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
    return sorted(out, key=lambda t: -t.px_per_mm), notes, small, dark, min_side


def find_stickers(rgb, side_mm=SIDE_MM, max_dim=1600, min_orient=0.5, min_frame=0.5):
    """All stickers found in an RGB photo (uint8), largest first. Empty list when none is found."""
    return _scan(rgb, side_mm, max_dim, min_orient, min_frame)[0]


def diagnose_stickers(rgb, side_mm=SIDE_MM, max_dim=1600, min_orient=0.5, min_frame=0.5):
    """find_stickers plus the reason when it finds nothing: a Diagnosis whose code is None if at least one sticker was accepted,
    otherwise the code of the most prominent (largest) frame-like shape that was turned down, or 'cut_by_edge' / 'no_candidate'
    when there was none."""
    out, notes, small, dark, min_side = _scan(rgb, side_mm, max_dim, min_orient, min_frame)
    if not out and not notes:
        notes = _scan(rgb, side_mm, max_dim, min_orient, min_frame, lenient=True)[1]
    best_fc = min(max(max((n[3] for n in notes), default=0.0), 0.0), 1.0)      # kept in 0..1: with a washed-out frame the raw ratio explodes
    best_oc = min(max(max((n[2] for n in notes), default=0.0), 0.0), 1.0)
    if out:
        return Diagnosis(out, None, best_fc, best_oc, len(notes))
    rejected = [n for n in notes if n[1] is not None]
    if rejected:
        code = max(rejected, key=lambda n: n[0])[1]
    elif _touches_border_dark(dark, small, min_side):
        code = "cut_by_edge"
    else:
        code = "no_candidate"
    return Diagnosis(out, code, best_fc, best_oc, len(notes))


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
