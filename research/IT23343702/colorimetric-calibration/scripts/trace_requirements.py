"""Requirements traceability: prints every requirement with its status and the tests behind it, and checks that every test named in
requirements.json exists.

    python scripts/trace_requirements.py            # table, exit 1 if a named test does not exist
    python scripts/trace_requirements.py --run      # also run the named tests of every requirement and show pass / fail
    python scripts/trace_requirements.py --file other.json

Statuses (meant plainly): covered = implemented and tested; partial = part of it is done or tested, the note says what is not;
deviation = deliberately different from the proposal, the note says why; open = not done or not measurable here, the note says why.
Evidence that does not run under pytest (the C++ port on the emulator, results on a phone) is listed in `evidence_outside_pytest`
and is not checked by this script.
"""

from __future__ import annotations

import argparse
import functools
import json
import re
import subprocess
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STATUSES = ("covered", "partial", "deviation", "open")


@functools.lru_cache(maxsize=1)
def collected_tests() -> frozenset[str]:
    """Every test id pytest can collect, as 'test_file.py::test_name' (parameters removed)."""
    out = subprocess.run([sys.executable, "-m", "pytest", "tests", "--collect-only", "-q", "-p", "no:cacheprovider"],
                         cwd=ROOT, capture_output=True, text=True, check=False).stdout
    ids = set()
    for line in out.splitlines():
        if "::" in line:
            ids.add(re.sub(r"\[.*\]$", "", line.strip()).removeprefix("tests/"))
    return frozenset(ids)


def missing_tests(reqs: list[dict], collected: frozenset[str]) -> dict[str, list[str]]:
    """requirement id -> named tests that do not exist. A name is 'file.py::test' or a whole 'file.py'."""
    bad: dict[str, list[str]] = {}
    for r in reqs:
        for t in r.get("tests", []):
            found = t in collected if "::" in t else any(c.startswith(t + "::") for c in collected)
            if not found:
                bad.setdefault(r["id"], []).append(t)
    return bad


def run_tests(ids: list[str]) -> bool:
    """True when every named test passes."""
    if not ids:
        return True
    r = subprocess.run([sys.executable, "-m", "pytest", *[f"tests/{i}" for i in ids], "-q", "-p", "no:cacheprovider"],
                       cwd=ROOT, capture_output=True, text=True, check=False)
    return r.returncode == 0


def render(reqs: list[dict], collected: frozenset[str], results: dict[str, bool] | None = None) -> tuple[str, dict[str, int]]:
    counts = {s: 0 for s in STATUSES}
    lines = [f"{'ID':6}{'status':11}{'tests':>6}  {'run':5} requirement", "-" * 100]
    for r in reqs:
        counts[r["status"]] += 1
        run = "" if results is None else ("pass" if results.get(r["id"]) else "FAIL") if r.get("tests") else "-"
        added = "  [added after PP1]" if r.get("added_after_pp1") else ""
        lines.append(f"{r['id']:6}{r['status']:11}{len(r.get('tests', [])):>6}  {run:5} {textwrap.shorten(r['text'], 62, placeholder='...')}{added}")
    lines += ["", "Status counts: " + ", ".join(f"{s} {n}" for s, n in counts.items()), "", "Notes (everything that is not fully covered):"]
    for r in reqs:
        if r["status"] != "covered":
            body = textwrap.fill(r["note"], 96, initial_indent="    ", subsequent_indent="    ")
            lines.append(f"  {r['id']} [{r['status']}]\n{body}")
            for e in r.get("evidence_outside_pytest", []):
                lines.append(textwrap.fill("evidence outside pytest: " + e, 96, initial_indent="    ", subsequent_indent="      "))
    return "\n".join(lines), counts


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--file", type=Path, default=ROOT / "requirements.json")
    ap.add_argument("--run", action="store_true", help="run the named tests of every requirement")
    args = ap.parse_args(argv)
    reqs = json.loads(args.file.read_text())["requirements"]
    collected = collected_tests()
    bad = missing_tests(reqs, collected)
    results = {r["id"]: run_tests(r.get("tests", [])) for r in reqs} if args.run else None
    table, _ = render(reqs, collected, results)
    print(table)
    for rid, names in bad.items():
        print(f"\nMISSING TEST for {rid}: {', '.join(names)}", file=sys.stderr)
    return 1 if bad or (results and not all(results.values())) else 0


if __name__ == "__main__":
    sys.exit(main())
