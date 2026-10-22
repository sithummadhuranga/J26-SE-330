"""Simulated photos of the printed sheet (make_sticker_sheet.py) with a known camera tilt, lighting and blur, to measure how well
sticker_finder.py finds the stickers and how accurate the area of the three red shapes of known area comes out.

The sheet picture is placed in a pinhole camera (4000 x 3000 pixels, 69 degree field of view, the sticker about 8-12 pixels per mm,
which is about 25-35 cm away), tilted by a chosen angle and turned by a random angle in the plane, then given exposure and colour
changes (linear light), blur, noise and JPEG compression. The truth is known: the camera model gives the real scale and corners,
and the shape areas are counted from the sheet picture.

Two areas are computed per shape from the nearest sticker: with the homography (corrects for tilt) and with one scale for the whole
photo (what a ruler can give). The sticker is 50-100 mm from the shapes on this sheet, which is about the distance to a wound.

    python area_simulation.py --sheet <sticker_sheet_a4.png> --out results.json [--trials 8]
"""
import argparse
import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sticker_finder as SF  # noqa: E402

W, H = 4000, 3000
F = (W / 2) / math.tan(math.radians(69 / 2))
SHEET_PPM = 600 / 25.4
SMALL_PPM = 15.0
P0 = (90.0, 110.0)  # the camera looks at this point of the sheet (mm)
SHAPES = {"rectangle": (35.0, 187.5), "circle": (90.0, 190.0), "triangle": (133.33, 185.0)}  # centroids on the sheet, mm
STICKER_ORIGINS = [(15 + 60 * c, 15 + 60 * r) for r in range(2) for c in range(3)]
BG = (120, 95, 80)


def srgb_lin(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def lin_srgb(x):
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(np.clip(x, 0, None), 1 / 2.4) - 0.055)


def red_mask(img):
    r, g, b = [img[..., i].astype(np.float32) for i in range(3)]
    return ((r > 1.6 * g) & (r > 1.6 * b) & (r > 30)).astype(np.uint8)


def truth_areas(sheet):
    """Area of each red shape, counted from the sheet picture as its own connected region."""
    m = red_mask(sheet)
    n, lab = cv2.connectedComponents(m)
    out = {}
    for name, (cx, cy) in SHAPES.items():
        best = min(range(1, n), key=lambda k: np.linalg.norm(np.argwhere(lab == k).mean(axis=0)[::-1] / SHEET_PPM - [cx, cy]))
        out[name] = float((lab == best).sum()) / SHEET_PPM ** 2
    return out


def pose_homography(tilt, rot, ppm):
    """Sheet plane (mm) to photo pixels. Plane coordinates are taken relative to P0."""
    t, r = math.radians(tilt), math.radians(rot)
    Rx = np.array([[1, 0, 0], [0, math.cos(t), -math.sin(t)], [0, math.sin(t), math.cos(t)]])
    Rz = np.array([[math.cos(r), -math.sin(r), 0], [math.sin(r), math.cos(r), 0], [0, 0, 1]])
    R = Rz @ Rx
    d = F / ppm
    K = np.array([[F, 0, W / 2], [0, F, H / 2], [0, 0, 1]])
    M = K @ np.column_stack([R[:, 0], R[:, 1], [0, 0, d]])
    shift = np.array([[1, 0, -P0[0]], [0, 1, -P0[1]], [0, 0, 1]])
    return M @ shift  # input: sheet mm (x, y, 1)


def render(sheet_small, Hmm, gain, wb, blur, noise, rng):
    to_mm = np.array([[1 / SMALL_PPM, 0, 0], [0, 1 / SMALL_PPM, 0], [0, 0, 1]])
    img = cv2.warpPerspective(sheet_small, Hmm @ to_mm, (W, H), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=BG)
    lin = srgb_lin(img.astype(np.float32) / 255.0) * (gain * np.array(wb, dtype=np.float32))
    if blur > 0:
        lin = cv2.GaussianBlur(np.clip(lin, 0, None).astype(np.float32), (0, 0), blur)   # a lens blurs light, not display values
    img = np.clip(lin_srgb(lin) * 255.0, 0, 255).astype(np.float32)
    img = np.clip(img + rng.normal(0, noise, img.shape).astype(np.float32), 0, 255).astype(np.uint8)
    ok, enc = cv2.imencode(".jpg", cv2.cvtColor(img, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 90])
    return cv2.cvtColor(cv2.imdecode(enc, cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB)


def project(Hmm, pts):
    return cv2.perspectiveTransform(np.array([pts], dtype=np.float64), Hmm)[0]


def local_ppm(Hmm, x, y):
    p = project(Hmm, [[x, y], [x + 1, y], [x, y + 1]])
    return float(math.sqrt(abs(np.linalg.det(np.array([p[1] - p[0], p[2] - p[0]]).T))))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sheet", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--trials", type=int, default=8)
    ap.add_argument("--tilts", default="0,10,20,30,40")
    a = ap.parse_args()
    sheet = cv2.cvtColor(cv2.imread(a.sheet), cv2.COLOR_BGR2RGB)
    truth = truth_areas(sheet)
    print("true areas counted from the sheet:", {k: round(v, 1) for k, v in truth.items()}, flush=True)
    q = SMALL_PPM / SHEET_PPM
    small = cv2.resize(sheet, None, fx=q, fy=q, interpolation=cv2.INTER_AREA)
    rng = np.random.default_rng(0)
    rows = []
    for tilt in [float(t) for t in a.tilts.split(",")]:
        for n in range(a.trials):
            rot, ppm = rng.uniform(0, 360), rng.uniform(8, 12)
            gain, blur, noise = float(np.exp(rng.uniform(np.log(0.5), np.log(1.6)))), rng.uniform(0.6, 1.4), rng.uniform(1, 4)
            wb = (rng.uniform(0.9, 1.1), 1.0, rng.uniform(0.9, 1.1))
            Hmm = pose_homography(tilt, rot, ppm)
            img = render(small, Hmm, gain, wb, blur, noise, rng)
            found = SF.find_stickers(img)
            row = dict(tilt=tilt, rot=rot, ppm=ppm, gain=gain, found=len(found))
            if not found:
                rows.append(row); continue
            centres = {i: project(Hmm, [[ox + 25, oy + 25]])[0] for i, (ox, oy) in enumerate(STICKER_ORIGINS)}
            cen_shapes = np.mean([project(Hmm, [[x, y]])[0] for x, y in SHAPES.values()], axis=0)
            # the detected sticker nearest to the shapes
            det = min(found, key=lambda s: np.linalg.norm(s.corners.mean(axis=0) - cen_shapes))
            tid = min(centres, key=lambda i: np.linalg.norm(centres[i] - det.corners.mean(axis=0)))
            ox, oy = STICKER_ORIGINS[tid]
            true_c = project(Hmm, [[ox, oy], [ox + 50, oy], [ox + 50, oy + 50], [ox, oy + 50]])
            row["corner_err_px"] = float(np.sqrt(((det.corners - true_c) ** 2).sum(axis=1)).mean())
            row["scale_err_pct"] = 100 * (det.px_per_mm - local_ppm(Hmm, ox + 25, oy + 25)) / local_ppm(Hmm, ox + 25, oy + 25)
            row["gap_mm"] = float(np.linalg.norm(np.array([ox + 25, oy + 25]) - np.mean(list(SHAPES.values()), axis=0)))
            m = red_mask(img)
            m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
            nlab, lab = cv2.connectedComponents(m)
            for name, (cx, cy) in SHAPES.items():
                cp = project(Hmm, [[cx, cy]])[0]
                cand = [(np.linalg.norm(np.argwhere(lab == k).mean(axis=0)[::-1] - cp), k) for k in range(1, nlab) if (lab == k).sum() > 2000]
                if not cand:
                    continue
                k = min(cand)[1]
                mk = (lab == k)
                row[name] = dict(hom=100 * (SF.area_mm2(mk, det) - truth[name]) / truth[name],
                                 uni=100 * (SF.area_uniform_mm2(mk, det) - truth[name]) / truth[name])
            rows.append(row)
        sub = [r for r in rows if r["tilt"] == tilt]
        ok = [r for r in sub if "corner_err_px" in r]
        hom = [abs(r[s]["hom"]) for r in ok for s in SHAPES if s in r]
        uni = [abs(r[s]["uni"]) for r in ok for s in SHAPES if s in r]
        print(f"tilt {tilt:4.0f} deg: sticker found in {len(ok)}/{len(sub)} photos | corner error median {np.median([r['corner_err_px'] for r in ok]):.2f} px | "
              f"scale error median {np.median([abs(r['scale_err_pct']) for r in ok]):.2f}% | area error |median| homography {np.median(hom):.2f}% (p90 {np.percentile(hom, 90):.2f}%), "
              f"one scale {np.median(uni):.2f}% (p90 {np.percentile(uni, 90):.2f}%)", flush=True)
    Path(a.out).write_text(json.dumps(dict(truth=truth, rows=rows), indent=1) + "\n")


if __name__ == "__main__":
    main()
