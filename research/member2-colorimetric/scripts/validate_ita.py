#!/usr/bin/env python3
"""Phase 1/2 validation script: run ITA classification over a folder of
sample images and report the FST distribution.

Usage:
    python scripts/validate_ita.py --images data/isic_subset --out data/annotations/ita_results.csv

If no image folder is given, or the folder is empty, falls back to a small
set of synthetic skin-tone swatches so the script is runnable on day one,
before any dataset has been downloaded (research guide Phase 1 item:
"Understand ITA formula and verify on sample images").
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from colorimetric.ita import classify_image  # noqa: E402

IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp"}

# Fallback swatches spanning the six FST bands, used when no dataset is
# available yet. Colors are rough sRGB approximations of the Fitzpatrick
# reference tones.
SYNTHETIC_SWATCHES = {
    "synthetic_FST_I.png": (245, 192, 165),
    "synthetic_FST_II.png": (230, 178, 148),
    "synthetic_FST_III.png": (172, 136, 120),
    "synthetic_FST_IV.png": (224, 138, 78),
    "synthetic_FST_V.png": (142, 106, 98),
    "synthetic_FST_VI.png": (95, 58, 38),
}


def load_images(image_dir: Path) -> list[tuple[str, np.ndarray]]:
    images = []
    if image_dir.exists():
        for path in sorted(image_dir.iterdir()):
            if path.suffix.lower() in IMAGE_EXTENSIONS:
                bgr = cv2.imread(str(path))
                if bgr is None:
                    print(f"  [skip] could not read {path.name}", file=sys.stderr)
                    continue
                images.append((path.name, cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)))
    return images


def synthetic_images() -> list[tuple[str, np.ndarray]]:
    images = []
    for name, color in SYNTHETIC_SWATCHES.items():
        patch = np.zeros((128, 128, 3), dtype=np.uint8)
        patch[:, :] = color
        images.append((name, patch))
    return images


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--images", type=Path, default=Path("data/isic_subset"))
    parser.add_argument("--out", type=Path, default=Path("data/annotations/ita_results.csv"))
    args = parser.parse_args()

    images = load_images(args.images)
    source = "dataset"
    if not images:
        print(
            f"No images found in {args.images} — falling back to synthetic FST swatches.\n"
            "Download an ISIC Archive subset into this folder for real validation "
            "(research guide, Phase 1: 'Download ISIC Archive subset').",
        )
        images = synthetic_images()
        source = "synthetic"

    args.out.parent.mkdir(parents=True, exist_ok=True)

    rows = []
    print(f"\n{'filename':<28}{'ITA (deg)':>12}{'FST':>6}{'skin px':>10}{'correction':>14}")
    print("-" * 72)
    for name, rgb in images:
        try:
            result = classify_image(rgb, min_skin_pixels=50)
        except ValueError as e:
            print(f"{name:<28}  ERROR: {e}")
            continue
        print(
            f"{name:<28}{result.ita_angle:>12.2f}{result.fitzpatrick_type.value:>6}"
            f"{result.skin_pixel_count:>10}{result.correction_strength:>14}"
        )
        rows.append(
            {
                "filename": name,
                "source": source,
                "ita_angle": round(result.ita_angle, 3),
                "fitzpatrick_type": result.fitzpatrick_type.value,
                "mean_L": round(result.mean_L, 3),
                "mean_b": round(result.mean_b, 3),
                "skin_pixel_count": result.skin_pixel_count,
                "correction_strength": result.correction_strength,
            }
        )

    with args.out.open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()) if rows else [])
        if rows:
            writer.writeheader()
            writer.writerows(rows)

    print(f"\nWrote {len(rows)} results to {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
