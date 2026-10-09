"""
One command to see that the backend is working: services up, health, then every non-disruptive end-to-end suite,
with a pass/fail summary at the end. Exit code 0 only if everything passed.

    python tests/check_backend.py              services + health + integration suites (about 5 minutes)
    python tests/check_backend.py --unit       also the .NET unit tests
    python tests/check_backend.py --mobile     also the phone's real sync engine against this backend (needs Flutter)
    python tests/check_backend.py --all        all of the above
    python tests/check_backend.py --quick      services + health + the smoke round trip only

Start the stack first: docker compose up -d --build   (and --profile tools up -d for the auth-health Prometheus checks)
The disruptive suites (e2e_architecture, e2e_orchestrator, e2e_retry) stop Kafka and restart services, so they are
never run from here; run them by hand when nothing else is using the stack.
"""
import json
import os
import re
import subprocess
import sys
import time
import urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
GATEWAY = os.environ.get("GATEWAY", "http://localhost:8080")

LONG_RUNNING = ["api-gateway", "identity-service", "sync-gateway", "ingest-persister", "outbox-relay", "orchestrator",
                "housekeeping", "rag-stub", "kafka", "postgres"]
ONE_SHOT = ["db-migrate", "kafka-init"]

# Order matters: e2e_db_roles right after the smoke test, while every service still holds a database connection.
SUITES = [
    ("e2e_smoke", "Round trip: login, push, Kafka, PostgreSQL, outbox, pull"),
    ("e2e_db_roles", "Least-privilege database roles"),
    ("e2e_step2_auth_admin", "Registration, MFA, lockout, audit trail"),
    ("e2e_device_revocation", "Device revocation (A1)"),
    ("e2e_housekeeping", "Housekeeping: no device misses a change (A2-A4)"),
    ("e2e_auth_health", "Auth-health metrics (A5)"),
    ("e2e_dashboard_client", "Admin dashboard sessions"),
    ("e2e_figures", "Figures proxy"),
    ("e2e_baseline", "REST baseline"),
]

GREEN, RED, YELLOW, RESET = ("\033[32m", "\033[31m", "\033[33m", "\033[0m") if sys.stdout.isatty() else ("",) * 4
results = []


def record(name, ok, detail=""):
    results.append((name, ok, detail))
    mark = f"{GREEN}PASS{RESET}" if ok else f"{RED}FAIL{RESET}"
    print(f"  {mark}  {name}" + (f"  ({detail})" if detail else ""), flush=True)


def check_services():
    print("\n[1] Services")
    out = subprocess.run(["docker", "compose", "ps", "-a", "--format", "json"], cwd=ROOT, capture_output=True, text=True)
    if out.returncode != 0:
        record("docker compose ps", False, out.stderr.strip()[:200] or "is Docker running?")
        return False
    rows = [json.loads(l) for l in out.stdout.splitlines() if l.strip().startswith("{")] if not out.stdout.lstrip().startswith("[") \
        else json.loads(out.stdout)
    by_service = {}
    for r in rows:
        by_service.setdefault(r["Service"], []).append(r)
    ok_all = True
    for svc in LONG_RUNNING:
        states = [r["State"] for r in by_service.get(svc, [])]
        ok = bool(states) and all(s == "running" for s in states)
        ok_all &= ok
        record(f"{svc} running", ok, ", ".join(states) or "not created; run docker compose up -d --build")
    for svc in ONE_SHOT:
        rs = by_service.get(svc, [])
        ok = bool(rs) and all(r["State"] == "exited" and r.get("ExitCode", 0) == 0 for r in rs)
        ok_all &= ok
        record(f"{svc} completed", ok, ", ".join(f"{r['State']} ({r.get('ExitCode')})" for r in rs) or "not created")
    return ok_all


def check_health():
    print("\n[2] Health through the API gateway")
    try:
        with urllib.request.urlopen(GATEWAY + "/health", timeout=5) as r:
            body = json.loads(r.read() or b"{}")
        record("GET /health", r.status == 200 and body.get("status") == "ok", f"{r.status} {body}")
        return True
    except OSError as e:
        record("GET /health", False, str(e))
        return False


def run_suite(script, label):
    path = os.path.join(ROOT, "tests", "integration", f"{script}.py")
    for attempt in (1, 2):
        start = time.time()
        out = subprocess.run([sys.executable, path], cwd=ROOT, capture_output=True, text=True)
        text = out.stdout + out.stderr
        m = re.search(r"(\d+) passed, (\d+) failed", text)
        passed, failed = (int(m.group(1)), int(m.group(2))) if m else (0, None)
        ok = out.returncode == 0 and failed == 0
        if ok or attempt == 2:
            break
        # Retry once to tell rate limits or dropped idle connections apart from a real failure.
        print(f"  {YELLOW}....{RESET}  {label}: failed, retrying once after the rate-limit window (65 s)", flush=True)
        time.sleep(65)
        if script == "e2e_db_roles":
            run_quiet("e2e_smoke")  # wake every service's database connection first
    detail = f"{passed} checks, {time.time() - start:.0f}s" + (", passed on retry" if ok and attempt == 2 else "")
    if not ok:
        fails = [l.strip() for l in text.splitlines() if l.strip().startswith("FAIL")][:3]
        detail = (f"{failed} failed: " + " | ".join(fails)) if fails else (text.strip().splitlines() or ["no output"])[-1][:200]
    record(f"{label}  [{script}]", ok, detail)


def run_quiet(script):
    subprocess.run([sys.executable, os.path.join(ROOT, "tests", "integration", f"{script}.py")], cwd=ROOT,
                   capture_output=True, text=True)


def run_unit():
    print("\n[4] .NET unit tests")
    out = subprocess.run(["dotnet", "test", "MelaninWoundCdss.slnx"], cwd=ROOT, capture_output=True, text=True)
    for line in out.stdout.splitlines():
        m = re.search(r"(Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+).* - (\S+)\.dll", line)
        if m:
            record(m.group(4), m.group(1) == "Passed" and m.group(2) == "0", f"{m.group(3)} passed, {m.group(2)} failed")
    if out.returncode != 0 and not any(r[0].endswith("Tests") for r in results):
        record("dotnet test", False, out.stdout.strip().splitlines()[-1][:200] if out.stdout.strip() else out.stderr[:200])


def run_mobile():
    print("\n[5] Phone sync engine against this backend (offline queue -> server -> advice on the device)")
    env = dict(os.environ, SYNC_BACKEND_URL=GATEWAY)
    flutter = "flutter.bat" if os.name == "nt" else "flutter"
    try:
        out = subprocess.run([flutter, "test", "--tags", "integration"], cwd=os.path.join(ROOT, "mobile"), env=env,
                             capture_output=True, text=True)
    except FileNotFoundError:
        record("flutter test --tags integration", False, "Flutter is not installed or not on PATH")
        return
    record("mobile round trip against the real backend", out.returncode == 0 and "All tests passed" in out.stdout,
           (out.stdout.strip().splitlines() or ["no output"])[-1][:200])


def main():
    args = set(sys.argv[1:])
    if args - {"--unit", "--mobile", "--all", "--quick"}:
        sys.exit(__doc__)
    started = time.time()
    print(f"Backend check against {GATEWAY}")

    services_ok = check_services()
    health_ok = check_health()
    if not (services_ok and health_ok):
        print(f"\n{RED}The stack is not up; fix the above before running the suites.{RESET}")
        print("  docker compose up -d --build\n  docker compose logs --tail 30 <service>")
        return 1

    print("\n[3] End-to-end suites")
    for script, label in (SUITES[:1] if "--quick" in args else SUITES):
        run_suite(script, label)
    if args & {"--unit", "--all"}:
        run_unit()
    if args & {"--mobile", "--all"}:
        run_mobile()

    failed = [r for r in results if not r[1]]
    print(f"\n{'=' * 72}")
    print(f"{len(results) - len(failed)}/{len(results)} checks passed in {time.time() - started:.0f}s")
    if failed:
        print(f"{RED}FAILED:{RESET}")
        for name, _, detail in failed:
            print(f"  - {name}: {detail}")
        return 1
    print(f"{GREEN}Backend is working.{RESET}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
