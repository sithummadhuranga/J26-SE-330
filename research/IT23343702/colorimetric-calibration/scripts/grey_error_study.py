"""Printed-grey error -> ITA shift on the labelled photos (Fitzpatrick17k, ISIC). Reply 6, section 4.

Light unchanged; the sticker grey is really L* = 50 + d while the reference says 50, so the correction applies a uniform
linear gain g = Y(50)/Y(50+d) to the photo. Reports the median signed ITA shift and the share of photos whose class changes.

Usage: .venv/bin/python scripts/grey_error_study.py
"""
import csv
import sys
from pathlib import Path
import cv2, numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.ita import classify_image, classify_fitzpatrick
from colorimetric.marker import apply_gains
D = Path.home() / "Documents/Color-pipeline/data"
T = ["I","II","III","IV","V","VI"]
Y = lambda L: ((L + 16) / 116) ** 3
sets = {
 "Fitzpatrick17k (n<=2413)": [(D/"fitzpatrick17k/images"/f"{r['md5hash']}.jpg", int(r["fitzpatrick_scale"])-1) for r in csv.DictReader(open(D/"fitzpatrick17k/download_log.csv")) if r["status"]=="ok"],
 "ISIC (n=200, my labels)": [(D/"isic_subset"/r["filename"], T.index(r["fitzpatrick_type"])) for r in csv.DictReader(open(D/"annotations/isic_subset_metadata.csv")) if r["fitzpatrick_type"] in T],
}
def ita(rgb):
    try: return classify_image(rgb).ita_angle
    except ValueError: return np.nan
for name, rows in sets.items():
    seen=set(); data=[]
    for p,k in rows:
        if p in seen or not p.exists(): continue
        seen.add(p); im=cv2.imread(str(p))
        if im is None: continue
        s=512/max(im.shape[:2]); im=cv2.resize(im,None,fx=s,fy=s,interpolation=cv2.INTER_AREA) if s<1 else im
        data.append((cv2.cvtColor(im,cv2.COLOR_BGR2RGB),k))
    y=np.array([k for _,k in data]); base=np.array([ita(i) for i,_ in data])
    print(f"\n## {name}: {len(data)} images, base ITA read on {int(np.isfinite(base).sum())}")
    for grp,sel in (("all",np.ones(len(y),bool)),("true I-III",y<=2),("true IV-VI",y>=3)):
        print(f" {grp}:")
        for d in (-6,-3,3,6):
            g=Y(50)/Y(50+d); gains=np.array([g,g,g])
            new=np.array([ita(apply_gains(i,gains)) for (i,_),s in zip(data,sel) if s])
            b=base[sel]; ok=np.isfinite(b)&np.isfinite(new); sh=(new-b)[ok]
            cls=lambda a:[classify_fitzpatrick(x).value for x in a]
            ch=np.mean([a!=c for a,c in zip(cls(b[ok]),cls(new[ok]))])
            print(f"   grey {d:+d} L*: median ITA shift {np.median(sh):+6.1f} deg  ({np.median(sh)/d:+.2f} deg per L*)  class changes {ch:4.0%}  n={ok.sum()}")
