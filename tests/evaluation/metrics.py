"""
The evaluation metrics from architecture §13, for one simulator run (device ids sim-<run>-NNN).

Device-side timestamps come from the simulator's CSV; server-side ones from audit.provenance (and the baseline
tables). Both clocks are this host's, which is why the simulator runs on the same machine as the stack.

    sync latency          device enqueue → PERSISTED (§13 definition); also → accepted, → advice stored, → delivered
    duplicates            rows per event_id in the store (0 by construction) and DEDUPLICATED provenance rows
                          (repeats the pipeline absorbed); baseline: extra rows per event
    ablation              with Ablation__Enabled, the shadow tables: extra rows a store without the unique
                          constraint, the gateway's DUPLICATE check and the inbox would hold (§13)
    auditability          events whose provenance has every expected stage / all events
    auth health           §13 table: login failure rate, lockouts per day, average session lifetime (auth_health)

Auth health on its own, for any period (default: the last 24 hours):

    python tests/evaluation/metrics.py auth [hours]
"""
import csv
import statistics
import subprocess


def psql_csv(query):
    out = subprocess.run(["docker", "exec", "postgres", "psql", "-U", "cdss", "-d", "cdss", "--csv", "-c", query],
                         capture_output=True, text=True, check=True)
    return list(csv.DictReader(out.stdout.splitlines()))


def percentiles(values):
    values = sorted(v for v in values if v is not None)
    if not values:
        return {"count": 0}
    at = lambda q: values[min(len(values) - 1, max(0, int(round(q * len(values) + 0.5)) - 1))]
    return {"count": len(values), "p50": round(at(0.50)), "p95": round(at(0.95)), "p99": round(at(0.99)),
            "max": round(values[-1]), "mean": round(statistics.fmean(values))}


def _ms(later, earlier):
    return None if not later or not earlier else (later - earlier).total_seconds() * 1000


def collect(run_id, mode, sim_csv_path, started_at, finished_at):
    """Returns (metrics dict, per-event rows) for one run."""
    from datetime import datetime
    parse = lambda s: datetime.fromisoformat(s) if s else None

    with open(sim_csv_path, newline="", encoding="utf-8") as f:
        device = {row["event_id"]: row for row in csv.DictReader(f)}
    pattern = f"sim-{run_id}-%"

    events = []
    if mode == "baseline":
        stored = psql_csv(f"""
            select a.event_id, count(*) as rows, min(a.received_at) as first_received, min(r.created_at) as advice_at
            from baseline.assessment a left join baseline.recommendation r on r.assessment_row_id = a.row_id
            where a.device_id like '{pattern}' group by a.event_id""")
        by_event = {s["event_id"]: s for s in stored}
        for event_id, d in device.items():
            s = by_event.get(event_id, {})
            enq = parse(d["enqueued_at"])
            events.append({"event_id": event_id, "status": d["status"], "attempts": int(d["attempts"]),
                           "stored_rows": int(s.get("rows", 0)),
                           "accept_ms": d["accept_ms"] or None,
                           "persisted_ms": _ms(parse(s.get("first_received")), enq),
                           "advice_stored_ms": _ms(parse(s.get("advice_at")), enq),
                           "delivered_ms": d["complete_ms"] or None})
        rows_total = sum(e["stored_rows"] for e in events)
        duplicates = {"stored_rows": rows_total, "distinct_events": sum(1 for e in events if e["stored_rows"]),
                      "extra_rows": rows_total - sum(1 for e in events if e["stored_rows"]),
                      "deduplicated_absorbed": 0}
        audit = {"applicable": False, "note": "the REST baseline writes no provenance (§13.1)"}
    else:
        stages = psql_csv(f"""
            select p.event_id, p.stage, min(p.recorded_at) as at, count(*) as n
            from audit.provenance p
            where p.device_id like '{pattern}'
               or p.event_id in (select event_id from clinical.wound_assessment where device_id like '{pattern}')
            group by p.event_id, p.stage""")
        per_event = {}
        for s in stages:
            per_event.setdefault(s["event_id"], {})[s["stage"]] = (parse(s["at"]), int(s["n"]))
        store = psql_csv(f"""
            select event_id, status, (select count(*) from clinical.recommendation r
                                      where r.assessment_id = w.assessment_id and r.revision = w.revision) as recs
            from clinical.wound_assessment w where device_id like '{pattern}'""")
        stored_rows = {}
        for s in store:
            stored_rows[s["event_id"]] = stored_rows.get(s["event_id"], 0) + 1

        complete = 0
        for event_id, d in device.items():
            st = per_event.get(event_id, {})
            enq = parse(d["enqueued_at"])
            at = lambda stage: st.get(stage, (None, 0))[0]
            superseded = d["status"] == "Superseded"
            expected = {"GATEWAY_ACCEPTED", "PERSISTED", "ORCHESTRATION_STARTED"} if superseded else \
                {"GATEWAY_ACCEPTED", "PERSISTED", "ORCHESTRATION_STARTED", "RAG_RETURNED", "RECOMMENDATION_STORED", "DELIVERED"}
            has_all = expected <= set(st)
            complete += has_all
            events.append({"event_id": event_id, "status": d["status"], "attempts": int(d["attempts"]),
                           "stored_rows": stored_rows.get(event_id, 0),
                           "accept_ms": d["accept_ms"] or None,
                           "persisted_ms": _ms(at("PERSISTED"), enq),
                           "advice_stored_ms": _ms(at("RECOMMENDATION_STORED"), enq),
                           # Use the originating phone's own completion time, since another phone may pull the advice first.
                           "delivered_ms": d["complete_ms"] or None,
                           "deduplicated": st.get("DEDUPLICATED", (None, 0))[1],
                           "audit_complete": has_all})
        rows_total = sum(e["stored_rows"] for e in events)
        duplicates = {"stored_rows": rows_total, "distinct_events": sum(1 for e in events if e["stored_rows"]),
                      "extra_rows": rows_total - sum(1 for e in events if e["stored_rows"]),
                      "deduplicated_absorbed": sum(e["deduplicated"] for e in events)}
        audit = {"applicable": True, "complete": complete, "events": len(device),
                 "completeness": round(complete / len(device), 4) if device else None}
        shadow = psql_csv(f"""
            select (select count(*) from ablation.wound_assessment where device_id like '{pattern}') as assessment_rows,
                   (select count(distinct event_id) from ablation.wound_assessment where device_id like '{pattern}')
                       as assessment_events,
                   (select count(*) from ablation.recommendation r where r.event_id in
                       (select event_id from clinical.wound_assessment where device_id like '{pattern}')) as recommendation_rows,
                   (select count(distinct r.event_id) from ablation.recommendation r where r.event_id in
                       (select event_id from clinical.wound_assessment where device_id like '{pattern}'))
                       as recommendation_events""")[0]
        shadow = {k: int(v) for k, v in shadow.items()}
        duplicates["ablation"] = {
            "recorded": shadow["assessment_rows"] > 0,
            "assessment_extra_rows": shadow["assessment_rows"] - shadow["assessment_events"],
            "recommendation_extra_rows": shadow["recommendation_rows"] - shadow["recommendation_events"],
            **shadow}

    auth = auth_health(started_at, finished_at)

    to_float = lambda v: float(v) if v not in (None, "") else None
    metrics = {
        "run_id": run_id, "mode": mode, "events": len(device),
        "by_status": {s: sum(1 for e in events if e["status"] == s) for s in sorted({e["status"] for e in events})},
        "device_resends": sum(max(0, e["attempts"] - 1) for e in events),
        "latency_ms": {
            "enqueue_to_accepted": percentiles(to_float(e["accept_ms"]) for e in events),
            "enqueue_to_persisted": percentiles(e["persisted_ms"] for e in events),
            "enqueue_to_advice_stored": percentiles(e["advice_stored_ms"] for e in events),
            "enqueue_to_delivered": percentiles(to_float(e["delivered_ms"]) for e in events),
        },
        "duplicates": duplicates,
        "auditability": audit,
        "auth": auth,
    }
    return metrics, events


def auth_health(since, until):
    """Auth-health metrics for [since, until]: login failure rate, lockouts per day and session lifetimes."""
    window = f"'{since.isoformat()}' and '{until.isoformat()}'"
    a = psql_csv(f"""
        select count(*) filter (where action = 'LOGIN' and success) as logins,
               count(*) filter (where (action = 'LOGIN' and not success and reason_code is distinct from 'MFA_REQUIRED')
                                   or action = 'LOCKOUT') as login_failures,
               count(*) filter (where action = 'LOCKOUT') as lockouts,
               count(*) filter (where action = 'LOGIN' and reason_code = 'MFA_REQUIRED') as mfa_prompts
        from audit.auth_audit where recorded_at between {window}""")[0]
    reasons = psql_csv(f"""
        select coalesce(reason_code, 'none') as reason, count(*) as n from audit.auth_audit
        where recorded_at between {window} and not success and action in ('LOGIN', 'LOCKOUT')
          and reason_code is distinct from 'MFA_REQUIRED'
        group by 1 order by 2 desc""")
    s = psql_csv(f"""
        with fam as (
            select family_id, min(issued_at) as started_at,
                   (array_agg(revoked_at order by issued_at desc))[1] as ended_at,
                   (array_agg(expires_at order by issued_at desc))[1] as expires_at
            from clinical.clinician_session group by family_id
            having min(issued_at) between {window})
        select count(*) as started,
               count(*) filter (where ended_at <= '{until.isoformat()}') as ended,
               count(*) filter (where (ended_at is null or ended_at > '{until.isoformat()}')
                                   and expires_at > '{until.isoformat()}') as active_at_end,
               round(avg(extract(epoch from ended_at - started_at)) filter (where ended_at <= '{until.isoformat()}'), 1)
                   as mean_lifetime_s,
               round((percentile_cont(0.5) within group (order by extract(epoch from ended_at - started_at))
                   filter (where ended_at <= '{until.isoformat()}'))::numeric, 1) as median_lifetime_s
        from fam""")[0]

    logins, failures, lockouts = int(a["logins"]), int(a["login_failures"]), int(a["lockouts"])
    days = max((until - since).total_seconds(), 1) / 86400
    num = lambda v: float(v) if v not in (None, "") else None
    return {
        "logins": logins, "login_failures": failures, "lockouts": lockouts, "mfa_prompts": int(a["mfa_prompts"]),
        "login_failure_rate": round(failures / (logins + failures), 4) if logins + failures else None,
        "lockouts_per_day": round(lockouts / days, 2),
        "failures_by_reason": {r["reason"]: int(r["n"]) for r in reasons},
        "sessions": {"started": int(s["started"]), "ended": int(s["ended"]), "active_at_end": int(s["active_at_end"]),
                     "mean_lifetime_s": num(s["mean_lifetime_s"]), "median_lifetime_s": num(s["median_lifetime_s"])},
    }


def write_events_csv(path, events):
    if not events:
        return
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(events[0].keys()))
        w.writeheader()
        w.writerows(events)


if __name__ == "__main__":
    import json
    import sys
    from datetime import datetime, timedelta, timezone

    if len(sys.argv) < 2 or sys.argv[1] != "auth":
        sys.exit(__doc__)
    hours = float(sys.argv[2]) if len(sys.argv) > 2 else 24
    until = datetime.now(timezone.utc)
    print(json.dumps(auth_health(until - timedelta(hours=hours), until), indent=2))
