"""Polite, resumable downloader for Fitzpatrick17k images (T15).

Reads fitzpatrick17k.csv (from github.com/mattgroh/fitzpatrick17k), downloads
images one at a time with a delay, skips dead links (many dermaamin.com links
are 404), and writes a log. Images are saved as <md5hash>.jpg so reruns skip
what is already there.

Licence: CC BY-NC-SA 3.0, scientific use only, delete after the project.
Keep the images out of git (data/ is outside the repo).

Usage (from research/member2-colorimetric):
    python scripts/download_fitzpatrick17k.py --per-fst 20          # small balanced sample
    python scripts/download_fitzpatrick17k.py --per-fst 0           # everything reachable
    python scripts/download_fitzpatrick17k.py --host dermaamin.com  # other source
"""

from __future__ import annotations

import argparse
import csv
import random
import sys
import time
import urllib.request
from collections import defaultdict
from pathlib import Path
from urllib.parse import urlparse

HEADERS = {"User-Agent": "Mozilla/5.0 (research; SLIIT J26-SE-330 student project)"}


def fetch(url: str, timeout: float) -> bytes | None:
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=timeout) as r:
            data = r.read()
            ctype = r.headers.get("content-type", "")
            return data if data and ctype.startswith("image") else None
    except Exception:
        return None


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--csv", type=Path, default=Path("data/fitzpatrick17k/fitzpatrick17k.csv"))
    p.add_argument("--out", type=Path, default=Path("data/fitzpatrick17k/images"))
    p.add_argument("--host", default="atlasdermatologico.com.br", help="only download URLs from this host ('' = all)")
    p.add_argument("--per-fst", type=int, default=20, help="images per FST 1-6 (0 = no limit)")
    p.add_argument("--delay", type=float, default=1.0, help="seconds between requests")
    p.add_argument("--timeout", type=float, default=15.0)
    p.add_argument("--seed", type=int, default=0)
    args = p.parse_args()

    if not args.csv.exists():
        print(f"CSV not found: {args.csv}", file=sys.stderr)
        return 1
    args.out.mkdir(parents=True, exist_ok=True)

    by_fst: dict[str, list[dict[str, str]]] = defaultdict(list)
    with args.csv.open(newline="") as f:
        for r in csv.DictReader(f):
            if r["fitzpatrick_scale"] not in {"1", "2", "3", "4", "5", "6"} or not r["url"]:
                continue
            if args.host and urlparse(r["url"]).hostname not in {args.host, f"www.{args.host}"}:
                continue
            by_fst[r["fitzpatrick_scale"]].append(r)

    rng = random.Random(args.seed)
    todo: list[dict[str, str]] = []
    for fst in sorted(by_fst):
        rows = by_fst[fst]
        rng.shuffle(rows)
        todo += rows[: args.per_fst] if args.per_fst else rows
    print({k: len(v) for k, v in sorted(by_fst.items())}, "available;", len(todo), "selected")

    log = args.out.parent / "download_log.csv"
    new_log = not log.exists()
    ok = fail = skipped = 0
    with log.open("a", newline="") as lf:
        w = csv.writer(lf)
        if new_log:
            w.writerow(["md5hash", "fitzpatrick_scale", "status", "url"])
        for i, r in enumerate(todo, 1):
            dest = args.out / f"{r['md5hash']}.jpg"
            if dest.exists():
                skipped += 1
                continue
            data = fetch(r["url"], args.timeout)
            if data is None:
                fail += 1
                w.writerow([r["md5hash"], r["fitzpatrick_scale"], "failed", r["url"]])
            else:
                dest.write_bytes(data)
                ok += 1
                w.writerow([r["md5hash"], r["fitzpatrick_scale"], "ok", r["url"]])
            lf.flush()
            if i % 25 == 0:
                print(f"{i}/{len(todo)}  ok={ok} failed={fail} skipped={skipped}")
            time.sleep(args.delay)
    print(f"Done. ok={ok} failed={fail} already-had={skipped}. Log: {log}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
