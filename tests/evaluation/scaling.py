"""
Scaling experiment (architecture §8.1, §13): the same load against 1, 2, 3 and 6 consumers of a group. Six partitions
per topic are what make six the ceiling.

    python tests/evaluation/scaling.py orchestrator     # Recommendation Service takes 1 s; replicas handle one at a time
    python tests/evaluation/scaling.py persister        # a burst of captures; persister replicas

For the orchestrator, each replica runs with Orchestrator__MaxConcurrency=1, so the replica count is the only
parallelism; a last run uses one replica with per-partition concurrency (the default) for comparison.
DISRUPTIVE: rescales services and restarts the rag-stub; everything is set back to one replica and defaults.
Results: tests/evaluation/results/<timestamp>-scaling-<group>/ and tests/evaluation/results/scaling.csv.
"""
import argparse
import csv
import os
import subprocess
import sys
import time
from datetime import datetime

sys.path.insert(0, os.path.dirname(__file__))
import run_experiment as rx  # noqa: E402

GROUPS = {
    # service, consumer group, simulator load, metric that reflects this consumer
    "orchestrator": {"service": "orchestrator", "group": "orchestrator", "devices": 20, "events": 6,
                     "capture_interval_ms": 200, "stub_delay_ms": "1000", "latency": "enqueue_to_delivered"},
    "persister": {"service": "ingest-persister", "group": "persister", "devices": 50, "events": 20,
                  "capture_interval_ms": 0, "stub_delay_ms": "0", "latency": "enqueue_to_persisted"},
}


def members(group):
    out = subprocess.run(["docker", "exec", "kafka", "/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server",
                          "localhost:9092", "--describe", "--group", group, "--members"],
                         capture_output=True, text=True).stdout
    return sum(1 for line in out.splitlines()[1:] if line.split()[:1] == [group])


def scale(service, replicas, concurrency=None):
    env = {"ORCHESTRATOR_MAX_CONCURRENCY": str(concurrency)} if concurrency else {}
    rx.compose("up", "-d", "--no-deps", "--force-recreate", "--scale", f"{service}={replicas}", service, env=env)


def drain_seconds(lag_csv, group):
    """Seconds from the first backlog until the group's lag is back to zero for good."""
    with open(lag_csv, newline="", encoding="utf-8") as f:
        rows = [(float(r["t_s"]), int(r[group] or 0)) for r in csv.DictReader(f)]
    busy = [t for t, lag in rows if lag > 0]
    if not busy:
        return 0.0
    after = [t for t, lag in rows if t > busy[-1] and lag == 0]
    return round((after[0] if after else busy[-1]) - busy[0], 1)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("group", choices=sorted(GROUPS))
    p.add_argument("--replicas", default="1,2,3,6")
    args = p.parse_args()
    g = GROUPS[args.group]

    out_dir = os.path.join(rx.RESULTS, f"{datetime.now().strftime('%Y%m%d-%H%M%S')}-scaling-{args.group}")
    os.makedirs(out_dir, exist_ok=True)
    subprocess.run(["dotnet", "build", "tests/device-simulator", "-v", "q", "-nologo"], cwd=rx.REPO_ROOT,
                   capture_output=True, check=True)

    runs = [(int(n), 1) for n in args.replicas.split(",")]
    if args.group == "orchestrator":
        runs.append((1, 6))   # one replica, per-partition concurrency
    rows = []
    try:
        for replicas, concurrency in runs:
            label = f"scale-{args.group}-x{replicas}" + (f"-c{concurrency}" if args.group == "orchestrator" else "")
            print(f"\n=== {label}")
            scale(g["service"], replicas, concurrency if args.group == "orchestrator" else None)
            if not rx.wait(lambda: members(g["group"]) == replicas, 120, 2):
                print(f"  warning: {members(g['group'])} members in {g['group']}, expected {replicas}")
            time.sleep(10)  # let the rebalance settle

            rx.SCENARIOS[label] = (lambda: rx.stub(delay_ms=g["stub_delay_ms"]), rx.nothing, rx.stub)
            run_args = argparse.Namespace(devices=g["devices"], events=g["events"], timeout=900,
                                          capture_interval_ms=g["capture_interval_ms"])
            r = rx.run_once(label, "event-driven", run_args, out_dir)
            lag_csv = os.path.join(out_dir, f"{r['run_id']}-event-driven-lag.csv")
            drain = drain_seconds(lag_csv, g["group"])
            lat = r["latency_ms"][g["latency"]]
            row = {"group": args.group, "replicas": replicas, "concurrency_per_replica": concurrency,
                   "events": r["events"], "finished": r["by_status"].get("Complete", 0) + r["by_status"].get("Superseded", 0),
                   "max_lag": r["max_lag"][g["group"]], "drain_s": drain,
                   "throughput_per_s": round(r["events"] / drain, 1) if drain else None,
                   "latency_metric": g["latency"], "latency_p50_ms": lat.get("p50"), "latency_p95_ms": lat.get("p95"),
                   "extra_rows": r["duplicates"]["extra_rows"],
                   "audit_completeness": r["auditability"].get("completeness"), "run_id": r["run_id"]}
            rows.append(row)
            print("  " + ", ".join(f"{k}={v}" for k, v in row.items() if k not in ("group", "run_id")))
            time.sleep(60)  # login rate-limit window
    finally:
        scale(g["service"], 1, 6 if args.group == "orchestrator" else None)
        rx.stub()

    path = os.path.join(rx.RESULTS, "scaling.csv")
    new = not os.path.exists(path)
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        if new:
            w.writeheader()
        w.writerows(rows)
    print(f"\nScaling rows appended to {path}")


if __name__ == "__main__":
    main()
