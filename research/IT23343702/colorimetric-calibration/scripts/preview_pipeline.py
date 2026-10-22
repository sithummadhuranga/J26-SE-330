"""Make a before/after contact sheet of process_image() on real images.

Each row is one image: original | Retinex only | Retinex + CCM | full pipeline.
The caption shows the ITA angle, FST estimate, and whether the CCM ran.

Usage (from research/IT23343702/colorimetric-calibration):
    python scripts/preview_pipeline.py --images data/isic_subset --out data/outputs/preview.png
    python scripts/preview_pipeline.py --files ISIC_0000001.jpg ISIC_0000085.jpg --gate-cutoff 14
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import replace
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric import ABLATION_VARIANTS, process_image  # noqa: E402

PANELS = [("original", None), ("retinex", "retinex"), ("retinex+ccm", "retinex_ccm"), ("full", "full")]
CELL_W = 320


def caption(img: np.ndarray, text: str) -> np.ndarray:
    bar = np.full((28, img.shape[1], 3), 30, dtype=np.uint8)
    cv2.putText(bar, text, (6, 19), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1, cv2.LINE_AA)
    return np.vstack([bar, img])


def row_for(path: Path, gate_cutoff: float | None) -> np.ndarray:
    rgb = cv2.cvtColor(cv2.imread(str(path)), cv2.COLOR_BGR2RGB)
    cells = []
    for title, variant in PANELS:
        if variant is None:
            out, info = rgb, path.name
        else:
            cfg = ABLATION_VARIANTS[variant]
            if gate_cutoff is not None:
                cfg = replace(cfg, gate_cutoff=gate_cutoff)
            res = process_image(rgb, cfg)
            ita = "n/a" if res.ita_angle is None else f"{res.ita_angle:.0f}deg"
            info = f"{title}  ITA {ita} {res.fst or '-'}  CCM {'yes' if res.ccm_applied else 'no'}"
        h = round(out.shape[0] * CELL_W / out.shape[1])
        cell = cv2.resize(out, (CELL_W, h), interpolation=cv2.INTER_AREA)
        cells.append(caption(cv2.cvtColor(cell, cv2.COLOR_RGB2BGR), info if variant else title + "  " + info))
    height = max(c.shape[0] for c in cells)
    cells = [cv2.copyMakeBorder(c, 0, height - c.shape[0], 0, 0, cv2.BORDER_CONSTANT, value=(30, 30, 30)) for c in cells]
    return np.hstack(cells)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--images", type=Path, default=Path("data/isic_subset"))
    p.add_argument("--files", nargs="*", help="specific file names inside --images")
    p.add_argument("--n", type=int, default=6, help="how many images when --files is not given")
    p.add_argument("--out", type=Path, default=Path("data/outputs/preview.png"))
    p.add_argument("--gate-cutoff", type=float, default=None, help="override the CCM gate (28 standard, 14 fitted)")
    args = p.parse_args()

    if args.files:
        paths = [args.images / f for f in args.files]
    else:
        all_paths = sorted(args.images.glob("*.jpg"))
        step = max(1, len(all_paths) // args.n)
        paths = all_paths[::step][: args.n]
    rows = [row_for(p_, args.gate_cutoff) for p_ in paths]
    width = max(r.shape[1] for r in rows)
    sheet = np.vstack([cv2.copyMakeBorder(r, 0, 4, 0, width - r.shape[1], cv2.BORDER_CONSTANT, value=(0, 0, 0)) for r in rows])
    args.out.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(args.out), sheet)
    print(f"Saved {args.out} ({len(paths)} images)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
