"""
Evaluation experiments (architecture §13, plan phase 10 part 2). Each scenario sets up a fault, runs the device
simulator through Toxiproxy (so every scenario uses the same network path), samples Kafka consumer lag, then writes
the §13 metrics. Everything is put back afterwards: toxics removed, stub and services restored.

DISRUPTIVE: local Docker stack only. Needs `docker compose --profile tools up -d toxiproxy` as well.

    python tests/evaluation/run_experiment.py <scenario> [--devices 20] [--events 10] [--modes event-driven,baseline]

Scenarios
    clean            no faults: the reference run
    latency          +300 ms ±100 ms each way on every connection (slow 3G-like link)
    loss             10 % of connections reset and 5 % stall until timeout (lossy Wi-Fi; Toxiproxy works per
                     connection, so this approximates packet loss — say so in the method, or use tc netem)
    flaky            the network drops for 8 s, back for 12 s, from 5 s in (disconnect cycles)
    slow-advice      Recommendation Service answers after 3 s
    consumer-kill    the persister and the orchestrator are killed mid-run and restarted 10 s later
    replay           after the run, both consumer groups are rewound to its start and consume everything again:
                     every message delivered twice (§11: relay republishes, crash before offset commit)

Results: tests/evaluation/results/<timestamp>-<scenario>/ (per mode: simulator CSV, per-event metrics CSV, metrics
JSON, consumer-lag CSV) and one row per run appended to tests/evaluation/results/summary.csv.
"""
import argparse
import csv
import json
import os
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "integration"))
from client import REPO_ROOT  # noqa: E402

import metrics  # noqa: E402
import toxiproxy  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")
RESULTS = os.path.join(REPO_ROOT, "tests", "evaluation", "results")
PROXIED_GATEWAY = "http://localhost:18080"
DIRECT_GATEWAY = "http://localhost:8080"   # setup only (registering clinicians): not part of what is measured


def compose(*args, env=None):
    return subprocess.run(["docker", "compose", *args], cwd=REPO_ROOT, capture_output=True, text=True, timeout=300,
                          env={**os.environ, **(env or {})})


def wait(fn, seconds, interval=1.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if fn():
                return True
        except Exception:
            pass
        time.sleep(interval)
    return False


def stub(mode="none", delay_ms="0"):
    compose("up", "-d", "--no-deps", "--force-recreate", "rag-stub", env={"STUB_FAIL_MODE": mode, "STUB_DELAY_MS": delay_ms})
    wait(lambda: subprocess.run(["curl", "-s", "-o", os.devnull, "-w", "%{http_code}", "http://localhost:5080/health"],
                                capture_output=True, text=True).stdout == "200", 60)


def group_lag(group):
    out = subprocess.run(["docker", "exec", "kafka", "/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server",
                          "localhost:9092", "--describe", "--group", group], capture_output=True, text=True).stdout
    lags = [int(c[5]) for c in (l.split() for l in out.splitlines()[1:]) if len(c) >= 6 and c[0] == group and c[5].isdigit()]
    return sum(lags) if lags else None


class LagSampler(threading.Thread):
    """Consumer lag over time (§13: 'consumer lag rises then drains'), every 2 s, for both consumer groups."""

    def __init__(self):
        super().__init__(daemon=True)
        self.samples, self.stop = [], threading.Event()

    def run(self):
        start = time.time()
        while not self.stop.is_set():
            self.samples.append({"t_s": round(time.time() - start, 1),
                                 "persister": group_lag("persister"), "orchestrator": group_lag("orchestrator")})
            self.stop.wait(2)


# ---- scenarios: (setup, during-run action, teardown) ------------------------------------------------------------

def nothing(*_):
    pass


def latency_setup():
    toxiproxy.add("lat-down", "latency", {"latency": 300, "jitter": 100}, "downstream")
    toxiproxy.add("lat-up", "latency", {"latency": 300, "jitter": 100}, "upstream")


def loss_setup():
    toxiproxy.add("reset", "reset_peer", {"timeout": 0}, "downstream", toxicity=0.10)
    toxiproxy.add("stall", "timeout", {"timeout": 5000}, "downstream", toxicity=0.05)


def flaky_during(done):
    """Down 8 s, up 12 s, starting 5 s in, until the devices are done."""
    if done.wait(5):
        return
    while True:
        toxiproxy.set_enabled(False)
        stop = done.wait(8)
        toxiproxy.set_enabled(True)
        if stop or done.wait(12):
            return


def slow_setup():
    stub(delay_ms="3000")


def slow_teardown():
    stub()


def kill_during(done):
    if done.wait(8):
        return
    compose("kill", "ingest-persister", "orchestrator")
    done.wait(10)
    compose("start", "ingest-persister", "orchestrator")


def replay_after(started):
    """Rewinds the persister and orchestrator to the run's start time, so only this run's messages repeat."""
    at = started.strftime("%Y-%m-%dT%H:%M:%S.000")
    compose("stop", "ingest-persister", "orchestrator")
    for group, topic in [("persister", "wound-events"), ("orchestrator", "wound-events.persisted")]:
        subprocess.run(["docker", "exec", "kafka", "/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server",
                        "localhost:9092", "--group", group, "--topic", topic, "--reset-offsets", "--to-datetime", at,
                        "--execute"], capture_output=True, text=True, check=True)
    compose("start", "ingest-persister", "orchestrator")
    time.sleep(10)


AFTER = {"replay": replay_after}

SCENARIOS = {
    "clean": (nothing, nothing, nothing),
    "latency": (latency_setup, nothing, nothing),
    "loss": (loss_setup, nothing, nothing),
    "flaky": (nothing, flaky_during, nothing),
    "slow-advice": (slow_setup, nothing, slow_teardown),
    "consumer-kill": (nothing, kill_during, lambda: compose("start", "ingest-persister", "orchestrator")),
    "replay": (nothing, nothing, nothing),
}


def run_once(scenario, mode, args, out_dir):
    setup, during, teardown = SCENARIOS[scenario]
    run_id = f"{scenario.replace('-', '')}{datetime.now().strftime('%H%M%S')}{'b' if mode == 'baseline' else 'e'}"
    toxiproxy.reset()
    # Start devices at the current cursor so the run measures only its own traffic (archive counts too).
    head = metrics.psql_csv("select greatest((select max(server_seq) from sync.change_log), "
                            "(select max(server_seq) from sync.change_log_archive), 0) as head")[0]["head"]
    setup()
    sampler, done = LagSampler(), threading.Event()
    action = threading.Thread(target=during, args=(done,), daemon=True)
    started = datetime.now(timezone.utc)
    sampler.start()
    action.start()
    try:
        sim = subprocess.run(
            ["dotnet", "run", "--no-build", "--project", "tests/device-simulator", "--",
             "--gateway", PROXIED_GATEWAY, "--setup-gateway", DIRECT_GATEWAY,
             # New TCP connection per request so Toxiproxy faults hit every request in every scenario.
             "--connection-lifetime-s", "0", "--start-cursor", head,
             "--capture-interval-ms", str(args.capture_interval_ms),
             "--devices", str(args.devices), "--events", str(args.events),
             "--mode", mode, "--run-id", run_id, "--timeout-s", str(args.timeout), "--out", out_dir],
            cwd=REPO_ROOT, capture_output=True, text=True)
    finally:
        done.set()
        action.join(timeout=60)
        toxiproxy.reset()
        teardown()
    # Let the pipeline drain before measuring (advice can still arrive after the devices stop).
    if mode == "event-driven":
        wait(lambda: group_lag("persister") == 0 and group_lag("orchestrator") == 0, 120, 2)
        if after := AFTER.get(scenario.removesuffix("-ablation")):
            after(started)
            wait(lambda: group_lag("persister") == 0 and group_lag("orchestrator") == 0, 300, 2)
            time.sleep(5)
    sampler.stop.set()
    sampler.join()
    finished = datetime.now(timezone.utc)

    sim_csv = os.path.join(out_dir, f"{run_id}-{'baseline' if mode == 'baseline' else 'eventdriven'}-events.csv")
    if not os.path.exists(sim_csv):
        print(sim.stdout[-2000:], sim.stderr[-2000:])
        raise SystemExit(f"simulator produced no results for {run_id}")
    result, events = metrics.collect(run_id, mode, sim_csv, started, finished)
    result.update({"scenario": scenario, "devices": args.devices, "events_per_device": args.events,
                   "simulator_exit": sim.returncode, "wall_s": round((finished - started).total_seconds(), 1),
                   "max_lag": {g: max((s[g] or 0) for s in sampler.samples) if sampler.samples else None
                               for g in ("persister", "orchestrator")}})

    base = os.path.join(out_dir, f"{run_id}-{mode}")
    metrics.write_events_csv(base + "-metrics-events.csv", events)
    with open(base + "-lag.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["t_s", "persister", "orchestrator"])
        w.writeheader()
        w.writerows(sampler.samples)
    with open(base + "-metrics.json", "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, default=str)
    append_summary(result)
    return result


def append_summary(r):
    path = os.path.join(RESULTS, "summary.csv")
    lat = r["latency_ms"]
    row = {
        "run_id": r["run_id"], "scenario": r["scenario"], "mode": r["mode"], "devices": r["devices"],
        "events": r["events"], "complete": r["by_status"].get("Complete", 0),
        "superseded": r["by_status"].get("Superseded", 0),
        "unfinished": r["events"] - r["by_status"].get("Complete", 0) - r["by_status"].get("Superseded", 0)
                      - r["by_status"].get("Rejected", 0),
        "device_resends": r["device_resends"],
        "accept_p50_ms": lat["enqueue_to_accepted"].get("p50"), "accept_p95_ms": lat["enqueue_to_accepted"].get("p95"),
        "persisted_p50_ms": lat["enqueue_to_persisted"].get("p50"), "persisted_p95_ms": lat["enqueue_to_persisted"].get("p95"),
        "delivered_p50_ms": lat["enqueue_to_delivered"].get("p50"), "delivered_p95_ms": lat["enqueue_to_delivered"].get("p95"),
        "extra_rows": r["duplicates"]["extra_rows"], "deduplicated_absorbed": r["duplicates"]["deduplicated_absorbed"],
        "ablation_assessment_extra_rows": r["duplicates"].get("ablation", {}).get("assessment_extra_rows", ""),
        "ablation_recommendation_extra_rows": r["duplicates"].get("ablation", {}).get("recommendation_extra_rows", ""),
        "audit_completeness": r["auditability"].get("completeness"),
        "login_failure_rate": r["auth"]["login_failure_rate"], "lockouts": r["auth"]["lockouts"],
        "max_lag_persister": r["max_lag"]["persister"], "max_lag_orchestrator": r["max_lag"]["orchestrator"],
        "wall_s": r["wall_s"],
    }
    rows = []
    if os.path.exists(path):
        with open(path, newline="", encoding="utf-8") as f:
            rows = list(csv.DictReader(f))
    fields = list(row.keys()) + [k for k in (rows[0].keys() if rows else []) if k not in row]
    # Rewritten whole, so a new column (e.g. the ablation counts) never misaligns older rows.
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields, restval="")
        w.writeheader()
        w.writerows(rows + [row])


def set_ablation(enabled):
    """§13 ablation: gateway DUPLICATE check and orchestrator inbox off, shadow rows recorded (see ablation/0001)."""
    compose("up", "-d", "--no-deps", "--force-recreate", "sync-gateway", "ingest-persister", "orchestrator",
            env={"ABLATION": "true" if enabled else "false"})
    marker = "ABLATION MODE"
    wait(lambda: (marker in compose("logs", "--since", "1m", "orchestrator").stdout) == enabled
         and group_lag("orchestrator") is not None, 90, 2)
    time.sleep(10)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("scenario", choices=sorted(SCENARIOS))
    p.add_argument("--devices", type=int, default=20)
    p.add_argument("--events", type=int, default=10)
    p.add_argument("--modes", default="event-driven,baseline")
    p.add_argument("--timeout", type=int, default=600, help="simulator timeout in seconds")
    p.add_argument("--ablation", action="store_true",
                   help="§13 duplicate ablation: protections off, shadow tables record what they absorbed")
    p.add_argument("--capture-interval-ms", type=int, default=2000,
                   help="time between captures on a device; long enough that faults overlap the run")
    args = p.parse_args()

    if subprocess.run(["curl", "-s", "-o", os.devnull, "-w", "%{http_code}", PROXIED_GATEWAY + "/health"],
                      capture_output=True, text=True).stdout != "200":
        raise SystemExit("Toxiproxy is not forwarding: docker compose --profile tools up -d toxiproxy")
    subprocess.run(["dotnet", "build", "tests/device-simulator", "-v", "q", "-nologo"], cwd=REPO_ROOT,
                   capture_output=True, check=True)

    label = args.scenario + ("-ablation" if args.ablation else "")
    out_dir = os.path.join(RESULTS, f"{datetime.now().strftime('%Y%m%d-%H%M%S')}-{label}")
    os.makedirs(out_dir, exist_ok=True)
    if args.ablation:
        SCENARIOS[label] = SCENARIOS[args.scenario]
        args.scenario = label
        set_ablation(True)
    try:
        run_modes(args, out_dir)
    finally:
        if args.ablation:
            set_ablation(False)


def run_modes(args, out_dir):
    for mode in args.modes.split(","):
        print(f"\n=== {args.scenario} / {mode}: {args.devices} devices × {args.events} events")
        r = run_once(args.scenario, mode, args, out_dir)
        lat = r["latency_ms"]
        print(json.dumps({"by_status": r["by_status"], "device_resends": r["device_resends"],
                          "accept_ms": lat["enqueue_to_accepted"], "persisted_ms": lat["enqueue_to_persisted"],
                          "delivered_ms": lat["enqueue_to_delivered"], "duplicates": r["duplicates"],
                          "auditability": r["auditability"], "max_lag": r["max_lag"], "wall_s": r["wall_s"]}, indent=2))
        if mode != args.modes.split(",")[-1]:
            time.sleep(60)  # let the login rate limit window pass between runs
    print(f"\nResults in {out_dir}\nSummary row(s) appended to {os.path.join(RESULTS, 'summary.csv')}")


if __name__ == "__main__":
    main()
