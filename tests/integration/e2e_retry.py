"""DISRUPTIVE: retries recover without blocking new events, and persistent failures end in the DLQ (about two minutes)."""
import os
import secrets
import subprocess
import sys
import time

from client import REPO_ROOT, Checks, create_clinician, http, login, sql, uuid7, wait_for, wound_event

sys.stdout.reconfigure(encoding="utf-8")

FIRST, SECOND = 20, 30
FAC = "fac-001"
DEVICE = "dev-retry-" + secrets.token_hex(3)
PASSWORD = "Retry-Pass-" + secrets.token_hex(4)
NURSE = "retry.nurse." + secrets.token_hex(3)

t = Checks()
check = t.check


def compose(*args, env=None):
    return subprocess.run(["docker", "compose", *args], cwd=REPO_ROOT, capture_output=True, text=True, timeout=300,
                          env={**os.environ, **(env or {})})


def wait(fn, seconds=60, interval=1.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if fn():
                return True
        except Exception:
            pass
        time.sleep(interval)
    return False


def stub(mode="none"):
    compose("up", "-d", "--no-deps", "--force-recreate", "rag-stub", env={"STUB_FAIL_MODE": mode})
    wait(lambda: subprocess.run(["curl", "-s", "-o", os.devnull, "-w", "%{http_code}", "http://localhost:5080/health"],
                                capture_output=True, text=True).stdout == "200", 60)


def orchestrator(first=None, second=None):
    env = {} if first is None else {"RETRY_FIRST_DELAY_SECONDS": str(first), "RETRY_SECOND_DELAY_SECONDS": str(second)}
    compose("up", "-d", "--no-deps", "--force-recreate", "orchestrator", env=env)
    wait(lambda: "Retry consumer on" in compose("logs", "--since", "2m", "orchestrator").stdout, 60)


def messages_for(topic, needle):
    out = subprocess.run(["docker", "exec", "kafka", "/opt/kafka/bin/kafka-console-consumer.sh", "--bootstrap-server",
                          "localhost:9092", "--topic", topic, "--from-beginning", "--timeout-ms", "8000",
                          "--property", "print.headers=true"], capture_output=True, text=True, timeout=90).stdout
    return [line for line in out.splitlines() if needle in line]


def push(evt):
    status, body = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": [evt]}, token)
    assert status == 200 and body["results"][0]["status"] == "ACCEPTED", (status, body)


def stored(assessment):
    return sql(f"select count(*) from clinical.recommendation where assessment_id = '{assessment}'") == "1"


print("Setup: orchestrator with retry delays of 20 s and 30 s")
create_clinician(NURSE, PASSWORD, "nurse", FAC, "Retry Nurse")
_, body = login(NURSE, PASSWORD, DEVICE)
token = body["accessToken"]
orchestrator(FIRST, SECOND)

try:
    # --------------------------------------------------------------------------------------------------------
    print("\n§10.3: a deferred event recovers after its delay, and does not block newer ones")
    stub("503")
    a1 = uuid7()
    e1 = wound_event(a1, uuid7(), DEVICE, FAC)
    push(e1)
    # Detected in the database (fast) so the newer event goes in well inside the first event's delay window.
    check("Recommendation Service down: event deferred (ADVICE_DEFERRED)",
          wait_for(f"select count(*) from sync.change_log where assessment_id = '{a1}' and change_type = 'ADVICE_DEFERRED'",
                   "1", 90))
    stub("none")
    a2 = uuid7()
    e2 = wound_event(a2, uuid7(), DEVICE, FAC)
    push(e2)
    check("a newer event is handled while the first one waits (no head-of-line blocking)", wait(lambda: stored(a2), 30))
    check("the deferred event is retried after its delay and gets its recommendation", wait(lambda: stored(a1), FIRST + 60))
    order = sql(f"""select string_agg(assessment_id::text, ',' order by created_at) from clinical.recommendation
                    where assessment_id in ('{a1}', '{a2}')""")
    check("... the newer event got its advice first", order == f"{a2},{a1}", order)
    check("... and the copy went through wound-events.retry.30s", messages_for("wound-events.retry.30s", e1["eventId"]))
    # Measured from the database, not from when this script spotted the retry message.
    waited = float(sql(f"""select extract(epoch from r.created_at - c.created_at) from sync.change_log c
                           join clinical.recommendation r using (assessment_id, revision)
                           where c.assessment_id = '{a1}' and c.change_type = 'ADVICE_DEFERRED'"""))
    check(f"... no sooner than the {FIRST} s delay", waited >= FIRST - 1, f"{waited:.1f} s")
    changes = sql(f"select string_agg(change_type, ',' order by server_seq) from sync.change_log where assessment_id = '{a1}'")
    check("the device saw PERSISTED, then ADVICE_DEFERRED, then RECOMMENDATION_READY",
          changes == "PERSISTED,ADVICE_DEFERRED,RECOMMENDATION_READY", changes)
    started = sql(f"select count(*) from audit.provenance where event_id = '{e1['eventId']}' and stage = 'ORCHESTRATION_STARTED'")
    check("both attempts are in the audit trail", started == "2", started)

    # --------------------------------------------------------------------------------------------------------
    print("\n§8.3 / §11: an event that keeps failing ends in the DLQ after both retry hops")
    stub("503")
    a3 = uuid7()
    e3 = wound_event(a3, uuid7(), DEVICE, FAC)
    push(e3)
    check("first failure → wound-events.retry.30s", wait(lambda: messages_for("wound-events.retry.30s", e3["eventId"]), 90))
    check("second failure → wound-events.retry.5m",
          wait(lambda: any("retry-count:2" in m for m in messages_for("wound-events.retry.5m", e3["eventId"])), FIRST + 90))
    dlq = []
    check("third failure → wound-events.dlq",
          wait(lambda: dlq.extend(messages_for("wound-events.dlq", e3["eventId"])) or dlq, SECOND + 90))
    head = dlq[0] if dlq else ""
    check("DLQ message keeps its history: 3 attempts, original topic, reason",
          "retry-count:3" in head and "original-topic:wound-events.persisted" in head and "dlq-reason:Deferred" in head,
          head[:400])
    check("no recommendation stored", not stored(a3))
    check("the device was told once that advice is deferred",
          sql(f"select count(*) from sync.change_log where assessment_id = '{a3}' and change_type = 'ADVICE_DEFERRED'") == "1")
finally:
    stub("none")
    orchestrator()

sys.exit(t.finish())
