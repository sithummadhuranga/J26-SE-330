"""DISRUPTIVE: tests the REST baseline's duplicates and waiting-for-advice behavior (local Docker only)."""
import os
import secrets
import subprocess
import sys
import time

from client import REPO_ROOT, Checks, create_clinician, http, login, sql, uuid7, wait_for, wound_event

sys.stdout.reconfigure(encoding="utf-8")

FAC = "fac-001"
DEVICE = "dev-base-" + secrets.token_hex(3)
PASSWORD = "Base-Pass-" + secrets.token_hex(4)
NURSE = "base.nurse." + secrets.token_hex(3)

t = Checks()
check = t.check


def stub(mode="none", delay_ms="0"):
    subprocess.run(["docker", "compose", "up", "-d", "--no-deps", "--force-recreate", "rag-stub"], cwd=REPO_ROOT,
                   capture_output=True, timeout=300, env={**os.environ, "STUB_FAIL_MODE": mode, "STUB_DELAY_MS": delay_ms})
    deadline = time.time() + 60
    while time.time() < deadline:
        if subprocess.run(["curl", "-s", "-o", os.devnull, "-w", "%{http_code}", "http://localhost:5080/health"],
                          capture_output=True, text=True).stdout == "200":
            return
        time.sleep(1)


def baseline(evt, tok=None):
    started = time.time()
    status, body = http("POST", "/v1/baseline/assessments", evt, tok or token)
    return status, body, time.time() - started


def rows(event_id):
    return sql(f"select count(*) from baseline.assessment where event_id = '{event_id}'")


print("Setup")
stub()
create_clinician(NURSE, PASSWORD, "nurse", FAC, "Baseline Nurse")
_, body = login(NURSE, PASSWORD, DEVICE)
token = body["accessToken"]

try:
    # --------------------------------------------------------------------------------------------------------
    print("\n§13.1: one request does everything")
    evt = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    status, body, _ = baseline(evt)
    check("valid assessment: 200 with the recommendation in the same response",
          status == 200 and body["recommendation"]["sections"][0]["citationTags"] == ["S1"], f"{status} {body}")
    check("assessment and recommendation stored in the baseline schema",
          rows(evt["eventId"]) == "1" and sql(f"""select count(*) from baseline.recommendation r join baseline.assessment a
                                                 on a.row_id = r.assessment_row_id where a.event_id = '{evt['eventId']}'""") == "1")
    check("the clinical record and the audit trail are untouched (separate schema)",
          sql(f"select count(*) from clinical.wound_assessment where event_id = '{evt['eventId']}'") == "0" and
          sql(f"select count(*) from audit.provenance where event_id = '{evt['eventId']}'") == "0")

    status, _, _ = baseline(wound_event(uuid7(), uuid7(), DEVICE, FAC), tok="not-a-token")
    check("no valid token: 401", status == 401, status)
    bad = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    del bad["clinicalAssessment"]["pedalPulses"]
    status, body, _ = baseline(bad)
    check("same validation as push: missing tri-state key is 400 SCHEMA_INVALID",
          status == 400 and body["code"] == "SCHEMA_INVALID", f"{status} {body}")
    status, body, _ = baseline(wound_event(uuid7(), uuid7(), "dev-someone-else", FAC))
    check("same identity rule as push: another device is 403", status == 403 and body["code"] == "DEVICE_MISMATCH", body)

    # --------------------------------------------------------------------------------------------------------
    print("\n§13 duplicates: the same request sent twice (a retry after a lost response)")
    dup = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    baseline(dup)
    baseline(dup)
    check("baseline stores it twice (no idempotency, by design)", rows(dup["eventId"]) == "2", rows(dup["eventId"]))
    same = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    _, first = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": [same]}, token)
    _, second = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": [same]}, token)
    check("event-driven push answers ACCEPTED then DUPLICATE",
          [first["results"][0]["status"], second["results"][0]["status"]] == ["ACCEPTED", "DUPLICATE"], (first, second))
    check("... and stores it once",
          wait_for(f"select count(*) from clinical.wound_assessment where event_id = '{same['eventId']}'", "1", 30))

    # --------------------------------------------------------------------------------------------------------
    print("\n§13 waiting: Recommendation Service takes 3 s")
    stub(delay_ms="3000")
    slow = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    status, body, took = baseline(slow)
    check("baseline: the device waits for the advice (3 s or more)", status == 200 and took >= 3, f"{status} {took:.2f}s")
    quick = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    started = time.time()
    status, body = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": [quick]}, token)
    took = time.time() - started
    check("event-driven push: ACCEPTED in under a second", status == 200 and took < 1, f"{status} {took:.2f}s")
    check("... and the advice still arrives afterwards (orchestrator)",
          wait_for(f"select count(*) from clinical.recommendation where assessment_id = '{quick['assessmentId']}'", "1", 60))

    # --------------------------------------------------------------------------------------------------------
    print("\n§11 vs baseline: Recommendation Service down (503)")
    stub("503")
    down = wound_event(uuid7(), uuid7(), DEVICE, FAC)
    status, body, _ = baseline(down)
    check("baseline: 502 RECOMMENDATION_FAILED", status == 502 and body["code"] == "RECOMMENDATION_FAILED", f"{status} {body}")
    check("... but the assessment was already written: a partial result without advice",
          rows(down["eventId"]) == "1" and sql(f"""select count(*) from baseline.recommendation r join baseline.assessment a
                                                  on a.row_id = r.assessment_row_id where a.event_id = '{down['eventId']}'""") == "0")
    baseline(down)
    check("the device retries: the assessment is stored again", rows(down["eventId"]) == "2", rows(down["eventId"]))

    stub("uncited")
    status, body, _ = baseline(wound_event(uuid7(), uuid7(), DEVICE, FAC))
    check("answer with an unresolved citation: 502 INVALID_RECOMMENDATION (same validation as the orchestrator)",
          status == 502 and body["code"] == "INVALID_RECOMMENDATION", f"{status} {body}")
finally:
    stub()

sys.exit(t.finish())
