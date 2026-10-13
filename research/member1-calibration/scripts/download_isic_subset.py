#!/usr/bin/env python3
"""Download a subset of ISIC Archive images for FST annotation.

Research guide, Phase 1: "Download ISIC Archive subset — start annotating
FST labels NOW (most time-consuming task)." Pulls public metadata + the
256px thumbnail (sufficient for skin-tone/FST classification; full-res
dermoscopic images are unnecessarily large for this purpose) via the
public ISIC API v2 (https://api.isic-archive.com/api/v2/images/).

Usage:
    python scripts/download_isic_subset.py --count 200 --out data/isic_subset
"""

from __future__ import annotations

import argparse
import csv
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

API_BASE = "https://api.isic-archive.com/api/v2/images/"


def fetch_page(url: str, timeout: int = 20) -> dict:
    req = urllib.request.Request(url, headers={"User-Agent": "color-pipeline/0.1 (research use)"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        import json

        return json.loads(resp.read().decode("utf-8"))


def download_file(url: str, dest: Path, timeout: int = 20, retries: int = 3) -> bool:
    if dest.exists():
        return True
    req = urllib.request.Request(url, headers={"User-Agent": "color-pipeline/0.1 (research use)"})
    for attempt in range(1, retries + 1):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp, dest.open("wb") as f:
                f.write(resp.read())
            return True
        except (urllib.error.URLError, TimeoutError) as e:
            if attempt == retries:
                print(f"  [fail] {dest.name}: {e}", file=sys.stderr)
                return False
            time.sleep(1.5 * attempt)
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--count", type=int, default=200, help="number of images to download")
    parser.add_argument("--out", type=Path, default=Path("data/isic_subset"))
    parser.add_argument(
        "--size",
        choices=["thumbnail_256", "full"],
        default="thumbnail_256",
        help="which asset to download (thumbnail is plenty for FST/ITA work)",
    )
    args = parser.parse_args()

    args.out.mkdir(parents=True, exist_ok=True)
    meta_path = args.out.parent / "annotations" / "isic_subset_metadata.csv"
    meta_path.parent.mkdir(parents=True, exist_ok=True)

    downloaded = 0
    rows = []
    url = f"{API_BASE}?limit=100"

    print(f"Downloading {args.count} ISIC images ({args.size}) into {args.out} ...")
    while url and downloaded < args.count:
        try:
            page = fetch_page(url)
        except (urllib.error.URLError, TimeoutError) as e:
            print(f"Failed to fetch page: {e}", file=sys.stderr)
            break

        for item in page["results"]:
            if downloaded >= args.count:
                break
            isic_id = item["isic_id"]
            files = item.get("files", {})
            asset = files.get(args.size) or files.get("thumbnail_256")
            if not asset:
                continue

            dest = args.out / f"{isic_id}.jpg"
            ok = download_file(asset["url"], dest)
            if not ok:
                continue

            clinical = item.get("metadata", {}).get("clinical", {})
            rows.append(
                {
                    "isic_id": isic_id,
                    "filename": dest.name,
                    "sex": clinical.get("sex", ""),
                    "age_approx": clinical.get("age_approx", ""),
                    "anatom_site": clinical.get("anatom_site_1", ""),
                    "diagnosis": clinical.get("diagnosis_1", ""),
                    "fitzpatrick_type": "",  # to be filled by validate_ita.py / manual review
                }
            )
            downloaded += 1
            if downloaded % 25 == 0:
                print(f"  {downloaded}/{args.count} downloaded")

        url = page.get("next")

    with meta_path.open("w", newline="") as f:
        writer = csv.DictWriter(
            f, fieldnames=["isic_id", "filename", "sex", "age_approx", "anatom_site", "diagnosis", "fitzpatrick_type"]
        )
        writer.writeheader()
        writer.writerows(rows)

    print(f"\nDownloaded {downloaded} images to {args.out}")
    print(f"Metadata written to {meta_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
