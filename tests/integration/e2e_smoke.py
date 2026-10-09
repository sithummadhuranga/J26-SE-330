"""Smoke test for the full push-to-advice round trip; needs the stack up and a demo clinician (n.silva)."""
import os
import sys

from client import Checks, http, login, pull_all, sql, uuid7, wait_for, wound_event

USERNAME = os.environ.get("E2E_USER", "n.silva")
PASSWORD = os.environ.get("E2E_PASSWORD", "Demo-Pass-2026!")
DEVICE = "dev-a41c"
FACILITY = "fac-001"

t = Checks()
check = t.check


def event(assessment, wound, **overrides):
    return wound_event(assessment, wound, DEVICE, FACILITY, **overrides)


print("Phase 1: auth")
status, body = login(USERNAME, PASSWORD, DEVICE)
check("login returns tokens", status == 200 and body and "accessToken" in body, f"{status} {body}")
if status != 200:
    sys.exit(f"Cannot continue without a login ({status} {body}). Is the clinician created?")
token, refresh = body["accessToken"], body["refreshToken"]

status, body = http("POST", "/v1/auth/refresh", {"refreshToken": refresh})
check("refresh rotates tokens", status == 200 and body["refreshToken"] != refresh, f"{status}")
old_refresh, refresh, token = refresh, body["refreshToken"], body["accessToken"]
status, _ = http("POST", "/v1/auth/refresh", {"refreshToken": old_refresh})
check("old refresh token is rejected after rotation", status == 401, f"{status}")

status, _ = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": [event(uuid7(), uuid7())]})
check("push without token is 401", status == 401, f"{status}")

print("Phase 2: push")
assessment, wound = uuid7(), uuid7()
good = event(assessment, wound)
invalid = event(uuid7(), wound, clinicalAssessment={"protectiveSensation": "absent"})
other_facility = event(uuid7(), wound, facilityId="fac-999")
status, body = http("POST", "/v1/sync/push",
                    {"deviceId": DEVICE, "batchId": "b1", "events": [good, invalid, good, other_facility]}, token)
statuses = [r["status"] for r in body["results"]] if body else []
check("batch returns a result per event", status == 200 and len(statuses) == 4, f"{status} {body}")
check("valid event ACCEPTED", statuses[:1] == ["ACCEPTED"], statuses)
check("missing tri-state key REJECTED", statuses[1:2] == ["REJECTED"] and body["results"][1]["code"] == "SCHEMA_INVALID", body)
check("repeat inside batch is DUPLICATE", statuses[2:3] == ["DUPLICATE"], statuses)
check("foreign facility REJECTED", statuses[3:4] == ["REJECTED"] and body["results"][3]["code"] == "FACILITY_MISMATCH", body)

status, body = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": [good]}, token)
check("re-sent event is DUPLICATE (reconnect case)", status == 200 and body["results"][0]["status"] == "DUPLICATE", body)

status, _ = http("POST", "/v1/sync/push", {"deviceId": "dev-other", "events": [good]}, token)
check("other device id in body is 403", status == 403, f"{status}")

print("Phase 3: persister")
eid = good["eventId"]
check("assessment persisted once",
      wait_for(f"select count(*) from clinical.wound_assessment where event_id = '{eid}'", "1"))
stages = sql(f"select string_agg(stage, ',' order by provenance_id) from audit.provenance where event_id = '{eid}'")
check("provenance GATEWAY_ACCEPTED,PERSISTED", stages.startswith("GATEWAY_ACCEPTED,PERSISTED"), stages)

print("Phase 4: outbox relay")
check("outbox row published", wait_for(
    f"select count(*) from messaging.outbox where topic = 'wound-events.persisted' and payload->>'eventId' = '{eid}' "
    f"and published_at is not null", "1"))

print("Phase 5: pull")
status, changes, cursor = pull_all(token)
found = [c for c in changes if c["assessmentId"] == assessment and c["type"] == "PERSISTED"]
check("PERSISTED change visible on pull", status == 200 and len(found) >= 1, f"{status}")
check("nextCursor advances", status == 200 and cursor >= found[0]["seq"] if found else False)

print("Phase 1: lockout (uses a second throwaway clinician if E2E_LOCKOUT_USER is set)")
lock_user = os.environ.get("E2E_LOCKOUT_USER")
if lock_user:
    for _ in range(5):
        login(lock_user, "wrong", DEVICE)
    status, body = login(lock_user, "wrong", DEVICE)
    check("5 failures lock the credential", status == 401 and body["code"] == "CREDENTIAL_LOCKED", body)
else:
    print("  skip  set E2E_LOCKOUT_USER to a throwaway clinician to test lockout")

status, _ = http("POST", "/v1/auth/logout", {"refreshToken": refresh})
status2, _ = http("POST", "/v1/auth/refresh", {"refreshToken": refresh})
check("logout revokes the session", status == 204 and status2 == 401, f"{status} {status2}")

sys.exit(t.finish())
