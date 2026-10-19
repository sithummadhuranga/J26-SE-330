"""How much dark skin do the HSV hue and saturation limits drop, and do wider limits help? (T-CV3 5.1)

Part A  Disagreement with a Lab skin proxy. There are NO pixel-level skin labels, so "true skin" is
        approximated by a Lab definition that does not use HSV: L* >= 15, chroma C*ab >= 5, hue angle
        h_ab in [PROXY_H]. This also keeps yellow-ish background and pale highlights, so the shares
        measure where the HSV mask and a Lab colour definition disagree, not error against annotation.
Part B  Outcome test. One change at a time from the shipped mask (H 0-25, S 15-80, V >= 20, mean):
        wider hue limit, hue wrap-around, lower saturation limit, higher saturation limit, and the
        median instead of the mean of L*, b*. Scored against the skin-type labels: exact, VI/V recall,
        and the 28 deg gate (dark recall IV-VI, light "false dark" I-III). No tuning on DFUC.
Images are resized to 512 px long side for speed (the shipped mask runs on the full image).

Usage: .venv/bin/python scripts/hue_sat_study.py
"""
from __future__ import annotations

import csv
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.ita import classify_fitzpatrick, compute_ita  # noqa: E402

D = Path.home() / "Documents/Color-pipeline/data"
T = ["I", "II", "III", "IV", "V", "VI"]
PROXY_H = (20.0, 80.0)
V_MIN = 20.0


def load_sets():
    f17 = [(D / "fitzpatrick17k/images" / f"{r['md5hash']}.jpg", int(r["fitzpatrick_scale"]) - 1)
           for r in csv.DictReader(open(D / "fitzpatrick17k/download_log.csv")) if r["status"] == "ok"]
    seen, uniq = set(), []
    for p, k in f17:
        if p not in seen and p.exists():
            seen.add(p); uniq.append((p, k))
    isic = [(D / "isic_subset" / r["filename"], T.index(r["fitzpatrick_type"]))
            for r in csv.DictReader(open(D / "annotations/isic_subset_metadata.csv")) if r["fitzpatrick_type"] in T]
    return {"Fitzpatrick17k": uniq, "ISIC (hand labels)": isic}


def prep(path: Path):
    bgr = cv2.imread(str(path))
    if bgr is None:
        return None
    s = 512 / max(bgr.shape[:2])
    if s < 1:
        bgr = cv2.resize(bgr, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV).astype(np.float32)
    lab = cv2.cvtColor(rgb, cv2.COLOR_RGB2LAB).astype(np.float32)
    return dict(h=hsv[..., 0] * 2, s=hsv[..., 1] / 2.55, v=hsv[..., 2] / 2.55,
                L=lab[..., 0] * 100 / 255, a=lab[..., 1] - 128, b=lab[..., 2] - 128)


def mask(p, h_lo=0.0, h_hi=25.0, wrap=False, s_lo=15.0, s_hi=80.0, v_lo=V_MIN):
    hue = (p["h"] >= h_lo) & (p["h"] <= h_hi)
    if wrap:
        hue |= p["h"] >= 340.0
    return hue & (p["s"] >= s_lo) & (p["s"] <= s_hi) & (p["v"] >= v_lo)


def ita_of(p, m, stat="mean"):
    if m.sum() < 100:
        return None
    f = np.mean if stat == "mean" else np.median
    return float(compute_ita(f(p["L"][m]), f(p["b"][m])))


VARIANTS = {
    "shipped (H 0-25, S 15-80, V>=20, mean)": dict(),
    "hue max 30": dict(h_hi=30.0), "hue max 35": dict(h_hi=35.0), "hue max 40": dict(h_hi=40.0),
    "hue wrap (340-360 also)": dict(wrap=True),
    "sat min 10": dict(s_lo=10.0), "sat min 5": dict(s_lo=5.0),
    "sat max 90": dict(s_hi=90.0), "sat max 100": dict(s_hi=100.0),
    "median instead of mean": dict(stat="median"),
    "hue 40 + sat 5-100 (all together)": dict(h_hi=40.0, s_lo=5.0, s_hi=100.0),
}


def main() -> None:
    for name, rows in load_sets().items():
        data = [(prep(p), k) for p, k in rows]
        data = [(p, k) for p, k in data if p is not None]
        y = np.array([k for _, k in data])
        print(f"\n## {name}: {len(y)} images; per type {[int((y == k).sum()) for k in range(6)]}")

        print("\nPart A: of Lab-proxy skin pixels, share the HSV mask excludes (median over images; V>=20 applied to both)")
        print("type  n   hue-only  sat-only  hue&sat  either | either: 90th pct, share of images losing >30%")
        for k in (3, 4, 5):
            rows_ = []
            for p, kk in data:
                if kk != k:
                    continue
                C = np.hypot(p["a"], p["b"])
                hab = np.degrees(np.arctan2(p["b"], p["a"])) % 360
                proxy = (p["L"] >= 15) & (C >= 5) & (hab >= PROXY_H[0]) & (hab <= PROXY_H[1]) & (p["v"] >= V_MIN)
                if proxy.sum() < 500:
                    continue
                bad_h = ~((p["h"] >= 0) & (p["h"] <= 25))
                bad_s = ~((p["s"] >= 15) & (p["s"] <= 80))
                n = proxy.sum()
                rows_.append([(proxy & bad_h & ~bad_s).sum() / n, (proxy & bad_s & ~bad_h).sum() / n,
                              (proxy & bad_h & bad_s).sum() / n, (proxy & (bad_h | bad_s)).sum() / n])
            if rows_:
                med = np.median(np.array(rows_), axis=0)
                eith = np.array(rows_)[:, 3]
                print(f"{T[k]:>4} {len(rows_):4d}   {med[0]:.2f}      {med[1]:.2f}     {med[2]:.2f}    {med[3]:.2f}  |   {np.percentile(eith, 90):.2f}   {np.mean(eith > 0.3):.2f}")

        print("\nPart B: one change at a time")
        print(f"{'variant':40s} noclass exact  within1 recV  recVI | dark rec(IV-VI) light false-dark(I-III) | changed class vs shipped")
        base_cls = None
        for vname, kw0 in VARIANTS.items():
            kw = dict(kw0)  # do not mutate the table: it is reused for the next dataset
            stat = kw.pop("stat", "mean")
            itas = [ita_of(p, mask(p, **kw), stat) for p, _ in data]
            ok = np.array([a is not None for a in itas])
            ita = np.array([a if a is not None else np.nan for a in itas])
            cls = np.array([T.index(classify_fitzpatrick(a).value) if a == a else -1 for a in ita])
            if base_cls is None:
                base_cls = cls.copy()
            t = y[ok]; c = cls[ok]; a = ita[ok]
            dark, light = t >= 3, t <= 2
            ch = int(((cls != base_cls) & ok).sum())
            print(f"{vname:40s} {int((~ok).sum()):5d}  {np.mean(c == t):.3f}  {np.mean(abs(c - t) <= 1):.3f}  "
                  f"{np.mean(c[t == 4] == 4) if (t == 4).any() else float('nan'):.2f}  "
                  f"{np.mean(c[t == 5] == 5) if (t == 5).any() else float('nan'):.2f} |"
                  f"      {np.mean(a[dark] <= 28):.3f}               {np.mean(a[light] <= 28):.3f}       | {ch}")


if __name__ == "__main__":
    main()
