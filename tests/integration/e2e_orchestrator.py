"""DISRUPTIVE: the orchestrator stores exactly one recommendation and routes failures to retry or DLQ (local Docker only)."""
import os
import secrets
import subprocess
import sys
import time

from client import REPO_ROOT, Checks, create_clinician, http, login, pull_all, sql, uuid7, wait_for, wound_event

sys.stdout.reconfigure(encoding="utf-8")

FAC = "fac-001"
DEVICE = "dev-orch-" + secrets.token_hex(3)
PASSWORD = "Orch-Pass-" + secrets.token_hex(4)
NURSE = "orch.nurse." + secrets.token_hex(3)

t = Checks()
check = t.check


def compose(*args, env=None):
    return subprocess.run(["docker", "compose", *args], cwd=REPO_ROOT, capture_output=True, text=True, timeout=300,
                          env={**os.environ, **(env or {})})


def kafka(*args, timeout=60):
    return subprocess.run(["docker", "exec", "kafka", *args], capture_output=True, text=True, timeout=timeout)


def stub(mode="none", delay_ms="0"):
    """Recreates the rag-stub with a failure mode (§13.1 fault injection) and waits until it answers."""
    compose("up", "-d", "--no-deps", "--force-recreate", "rag-stub", env={"STUB_FAIL_MODE": mode, "STUB_DELAY_MS": delay_ms})
    wait(lambda: subprocess.run(["curl", "-s", "-o", os.devnull, "-w", "%{http_code}", "http://localhost:5080/health"],
                                capture_output=True, text=True).stdout == "200", 60)


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


def one(column_sql):
    return sql(column_sql)


def push(*events):
    status, body = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": list(events)}, token)
    assert status == 200, (status, body)
    return body


def stages(event_id):
    return set(one(f"select string_agg(distinct stage, ',') from audit.provenance where event_id = '{event_id}'").split(","))


def messages_for(topic, needle):
    out = kafka("/opt/kafka/bin/kafka-console-consumer.sh", "--bootstrap-server", "localhost:9092", "--topic", topic,
                "--from-beginning", "--timeout-ms", "10000", "--property", "print.headers=true", timeout=90).stdout
    return [line for line in out.splitlines() if needle in line]


def recommendations(assessment):
    return one(f"select count(*) from clinical.recommendation where assessment_id = '{assessment}'")


# ------------------------------------------------------------------------------------------------------------
print("Setup")
stub()
create_clinician(NURSE, PASSWORD, "nurse", FAC, "Orchestrator Nurse")
_, body = login(NURSE, PASSWORD, DEVICE)
token = body["accessToken"]

try:
    # --------------------------------------------------------------------------------------------------------
    print("\n§2.2 steps 7-10: the full round trip")
    a, w = uuid7(), uuid7()
    evt = wound_event(a, w, DEVICE, FAC)
    push(evt)
    check("recommendation stored for the assessment",
          wait_for(f"select count(*) from clinical.recommendation where assessment_id = '{a}'", "1", 60))
    row = one(f"select mode || '|' || corpus_version || '|' || (payload->'sections'->0->'citationTags'->>0) "
              f"from clinical.recommendation where assessment_id = '{a}'")
    check("the service's answer is stored unchanged (mode, corpus version, cited section)", row == "extractive|stub-0|S1", row)
    _, changes, _ = pull_all(token)
    ready = [c for c in changes if c["assessmentId"] == a and c["type"] == "RECOMMENDATION_READY"]
    check("device pulls RECOMMENDATION_READY with the recommendation", len(ready) >= 1 and
          ready[0]["recommendation"]["sections"][0]["heading"] == "Stub guidance", ready)
    expected = {"GATEWAY_ACCEPTED", "PERSISTED", "ORCHESTRATION_STARTED", "RAG_RETURNED", "RECOMMENDATION_STORED", "DELIVERED"}
    check("every provenance stage from §12 is recorded, ending with DELIVERED", expected <= stages(evt["eventId"]),
          stages(evt["eventId"]))
    traces = one(f"select count(distinct trace_id) from audit.provenance where event_id = '{evt['eventId']}' "
                 f"and stage in ('GATEWAY_ACCEPTED', 'ORCHESTRATION_STARTED', 'RECOMMENDATION_STORED')")
    check("one trace id from the gateway through the orchestrator", traces == "1", traces)
    check("RAG_RETURNED carries the corpus version as audit reference",
          one(f"select rag_audit_ref from audit.provenance where event_id = '{evt['eventId']}' and stage = 'RAG_RETURNED'")
          == "stub-0/extractive/hybrid")
    pull_all(token)
    check("pulling again does not add a second DELIVERED row",
          one(f"select count(*) from audit.provenance where event_id = '{evt['eventId']}' and stage = 'DELIVERED'") == "1")
    check("recommendations.ready published through the outbox", wait(lambda: messages_for("recommendations.ready", a), 30))
    check("the inbox marks the event as processed by the orchestrator",
          one(f"select count(*) from messaging.inbox where consumer_name = 'orchestrator' and event_id = '{evt['eventId']}'") == "1")

    # --------------------------------------------------------------------------------------------------------
    print("\n§10.1 SupersedeCheck: two revisions pushed together")
    a2, w2 = uuid7(), uuid7()
    r1 = wound_event(a2, w2, DEVICE, FAC, revision=1)
    r2 = wound_event(a2, w2, DEVICE, FAC, revision=2, patientRef=r1["patientRef"])
    push(r1, r2)
    check("only the newest revision gets a recommendation",
          wait_for(f"select string_agg(revision::text, ',') from clinical.recommendation where assessment_id = '{a2}'", "2", 60))
    check("revision 1 is marked SUPERSEDED",
          wait_for(f"select status from clinical.wound_assessment where event_id = '{r1['eventId']}'", "SUPERSEDED", 30))
    check("... and the device gets a SUPERSEDED change for it",
          one(f"select count(*) from sync.change_log where assessment_id = '{a2}' and revision = 1 and change_type = 'SUPERSEDED'") == "1")

    # --------------------------------------------------------------------------------------------------------
    print("\n§11: replaying wound-events.persisted stores nothing twice")
    time.sleep(3)
    # Only check events that already had an answer; unfinished ones can legitimately complete on replay.
    cutoff = one("select now()")
    answered = f"(select event_id from audit.provenance where stage = 'RAG_RETURNED' and recorded_at < '{cutoff}')"
    rag_before = one(f"select count(*) from audit.provenance where stage = 'RAG_RETURNED' and event_id in {answered}")
    compose("stop", "orchestrator")
    kafka("/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server", "localhost:9092", "--group", "orchestrator",
          "--reset-offsets", "--to-earliest", "--topic", "wound-events.persisted", "--execute")
    compose("start", "orchestrator")

    def drained():
        out = kafka("/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server", "localhost:9092", "--describe",
                    "--group", "orchestrator").stdout
        rows = [l.split() for l in out.splitlines()[1:] if l.split()[:2] == ["orchestrator", "wound-events.persisted"]]
        return len(rows) == 6 and all(r[5] == "0" for r in rows)

    check("orchestrator works through the whole topic again", wait(drained, 300, 3))
    check("events that already had an answer: the Recommendation Service was not asked again (inbox stops repeats)",
          one(f"select count(*) from audit.provenance where stage = 'RAG_RETURNED' and event_id in {answered}") == rag_before,
          rag_before)
    check("no event anywhere has two Recommendation Service answers",
          one("select count(*) from (select event_id from audit.provenance where stage = 'RAG_RETURNED' "
              "group by 1 having count(*) > 1) x") == "0")
    check("no (assessment, revision) has two recommendations",
          one("select count(*) from (select 1 from clinical.recommendation group by assessment_id, revision "
              "having count(*) > 1) x") == "0")

    # --------------------------------------------------------------------------------------------------------
    print("\n§11: orchestrator killed in the middle of a Recommendation Service call")
    stub(delay_ms="10000")
    a3, w3 = uuid7(), uuid7()
    evt3 = wound_event(a3, w3, DEVICE, FAC)
    push(evt3)
    check("orchestration started",
          wait_for(f"select count(*) > 0 from audit.provenance where event_id = '{evt3['eventId']}' and stage = 'ORCHESTRATION_STARTED'", "t", 60))
    compose("kill", "orchestrator")
    check("killed before the answer was stored", recommendations(a3) == "0")
    stub()
    compose("start", "orchestrator")
    check("after restart the message is redelivered and exactly one recommendation is stored",
          wait_for(f"select count(*) from clinical.recommendation where assessment_id = '{a3}'", "1", 120))
    time.sleep(5)
    check("... and still exactly one a little later", recommendations(a3) == "1")

    # --------------------------------------------------------------------------------------------------------
    print("\n§10.3: Recommendation Service unavailable (503) → ADVICE_DEFERRED and retry topic")
    stub("503")
    a4, w4 = uuid7(), uuid7()
    evt4 = wound_event(a4, w4, DEVICE, FAC)
    push(evt4)
    check("ADVICE_DEFERRED change for the device",
          wait_for(f"select count(*) from sync.change_log where assessment_id = '{a4}' and change_type = 'ADVICE_DEFERRED'", "1", 90))
    _, changes, _ = pull_all(token)
    check("device pulls ADVICE_DEFERRED", any(c["assessmentId"] == a4 and c["type"] == "ADVICE_DEFERRED" for c in changes))
    retry = messages_for("wound-events.retry.30s", evt4["eventId"])
    check("message copied to wound-events.retry.30s with retry-count 1 and the original topic",
          len(retry) == 1 and "retry-count:1" in retry[0] and "original-topic:wound-events.persisted" in retry[0], retry)
    check("no recommendation stored", recommendations(a4) == "0")

    # --------------------------------------------------------------------------------------------------------
    print("\n§10.3: contract error (422) → straight to the DLQ")
    stub("422")
    a5, w5 = uuid7(), uuid7()
    evt5 = wound_event(a5, w5, DEVICE, FAC)
    push(evt5)
    check("message in wound-events.dlq with the reason",
          wait(lambda: any("dlq-reason:DeadLetter" in m for m in messages_for("wound-events.dlq", evt5["eventId"])), 60))
    check("never sent to a retry topic", not messages_for("wound-events.retry.30s", evt5["eventId"]))
    check("ADVICE_DEFERRED for the device",
          one(f"select count(*) from sync.change_log where assessment_id = '{a5}' and change_type = 'ADVICE_DEFERRED'") == "1")

    # --------------------------------------------------------------------------------------------------------
    print("\n§10.1 ValidateResponse: answer with an unresolved citation")
    stub("uncited")
    a6, w6 = uuid7(), uuid7()
    evt6 = wound_event(a6, w6, DEVICE, FAC)
    push(evt6)
    check("rejected and sent to the retry topic with the reason",
          wait(lambda: any("INVALID_RESPONSE" in m and "S9" in m for m in messages_for("wound-events.retry.30s", evt6["eventId"])), 60))
    check("nothing stored", recommendations(a6) == "0")
finally:
    stub()
    compose("start", "orchestrator")

sys.exit(t.finish())
