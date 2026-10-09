"""A revoked phone can't log in or refresh, and the gateway refuses its tokens within 30 s; others are unaffected."""
import secrets
import sys
import time

from client import Checks, create_clinician, http, login, sql, uuid7, wound_event

FAC_A, FAC_B = "fac-001", "fac-002"
suffix = secrets.token_hex(3)
LOST, KEPT, ADMIN_DEVICE = f"dev-lost-{suffix}", f"dev-kept-{suffix}", f"dev-admin-{suffix}"
PASSWORD = "Revoke-Pass-" + secrets.token_hex(4)
ADMIN_A, ADMIN_B, NURSE = f"admin.a.{suffix}", f"admin.b.{suffix}", f"nurse.{suffix}"
CACHE_SECONDS = 30  # DeviceRevocationCheck.CacheFor in the Sync Gateway

t = Checks()
check = t.check

print("Setup: an admin per facility, a nurse with two phones")
sql(f"INSERT INTO clinical.facility (facility_id, name) VALUES ('{FAC_B}', 'Second Test Hospital') ON CONFLICT DO NOTHING")
create_clinician(ADMIN_A, PASSWORD, "admin", FAC_A, "Admin A")
create_clinician(ADMIN_B, PASSWORD, "admin", FAC_B, "Admin B")
create_clinician(NURSE, PASSWORD, "nurse", FAC_A, "Nurse")
admin_a = login(ADMIN_A, PASSWORD, ADMIN_DEVICE)[1]["accessToken"]
admin_b = login(ADMIN_B, PASSWORD, "dev-admin-b-" + suffix)[1]["accessToken"]
_, lost = login(NURSE, PASSWORD, LOST)
_, kept = login(NURSE, PASSWORD, KEPT)

status, _ = http("GET", "/v1/sync/changes?cursor=0&limit=1", token=lost["accessToken"])
check("the phone can pull before revocation", status == 200, status)

print("Listing and permissions")
status, body = http("GET", "/v1/admin/devices", token=admin_a)
mine = {d["deviceId"]: d for d in body} if status == 200 else {}
check("admin lists the facility's devices", LOST in mine and KEPT in mine, f"{status}")
check("... with active sessions and the last user",
      mine.get(LOST, {}).get("activeSessions") == 1 and mine.get(LOST, {}).get("lastUsername") == NURSE, mine.get(LOST))
status, body = http("GET", "/v1/admin/devices", token=admin_b)
check("another facility's admin does not see them", status == 200 and LOST not in {d["deviceId"] for d in body})
status, _ = http("POST", f"/v1/admin/devices/{LOST}/revoke", token=admin_b)
check("another facility's admin gets 404, not 403", status == 404, status)
status, _ = http("POST", f"/v1/admin/devices/{LOST}/revoke", token=kept["accessToken"])
check("a nurse cannot revoke devices (403)", status == 403, status)
status, body = http("POST", "/v1/admin/devices/dev-does-not-exist/revoke", token=admin_a)
check("unknown device is 404", status == 404 and body["code"] == "NOT_FOUND", f"{status} {body}")

print("Revocation")
status, _ = http("POST", f"/v1/admin/devices/{LOST}/revoke", token=admin_a)
revoked_at = time.monotonic()
check("admin revokes the lost phone (204)", status == 204, status)
status, body = http("POST", f"/v1/admin/devices/{LOST}/revoke", token=admin_a)
check("revoking again is 409", status == 409 and body["code"] == "DEVICE_ALREADY_REVOKED", f"{status} {body}")
check("every session on the phone is ended",
      sql(f"select count(*) from clinical.clinician_session where device_id='{LOST}' and revoked_at is null") == "0")

status, _ = http("POST", "/v1/auth/refresh", {"refreshToken": lost["refreshToken"]})
check("its refresh token stops working at once", status == 401, status)
status, body = login(NURSE, PASSWORD, LOST)
check("nobody can log in on it", status == 401 and body["code"] == "DEVICE_NOT_ALLOWED", f"{status} {body}")

# The access token is still validly signed; the gateway refuses it once its cached answer expires.
refused_after = None
while time.monotonic() - revoked_at < CACHE_SECONDS + 10:
    status, _ = http("GET", "/v1/sync/changes?cursor=0&limit=1", token=lost["accessToken"])
    if status == 401:
        refused_after = time.monotonic() - revoked_at
        break
    time.sleep(2)
check(f"its unexpired access token is refused within {CACHE_SECONDS} s (pull)",
      refused_after is not None and refused_after <= CACHE_SECONDS + 3, refused_after)
evt = wound_event(uuid7(), uuid7(), LOST, FAC_A)
status, _ = http("POST", "/v1/sync/push", {"deviceId": LOST, "events": [evt]}, lost["accessToken"])
check("... and for push", status == 401, status)

print("Nothing else is affected")
status, _ = http("GET", "/v1/sync/changes?cursor=0&limit=1", token=kept["accessToken"])
check("the nurse's other phone still pulls", status == 200, status)
status, _ = http("POST", "/v1/auth/refresh", {"refreshToken": kept["refreshToken"]})
check("... and refreshes", status == 200, status)
status, _ = login(NURSE, PASSWORD, KEPT)
check("the nurse can still log in on another phone", status == 200, status)
status, body = http("GET", "/v1/admin/devices", token=admin_a)
row = next((d for d in body if d["deviceId"] == LOST), {}) if status == 200 else {}
check("the list shows it revoked with no active sessions", row.get("revokedAt") and row.get("activeSessions") == 0, row)

print("Audit trail")
status, body = http("GET", "/v1/admin/auth-audit?limit=500", token=admin_a)
rows = [e for e in body if e["action"] == "DEVICE_REVOKE" and e["deviceId"] == LOST] if status == 200 else []
check("DEVICE_REVOKE is audited once, with the admin as actor",
      len(rows) == 1 and rows[0]["actorUsername"] == ADMIN_A and rows[0]["success"], rows)
denied = [e for e in body if e["deviceId"] == LOST and e["reasonCode"] == "DEVICE_NOT_ALLOWED"] if status == 200 else []
check("the refused login on it is audited", len(denied) >= 1)
status, body = http("GET", "/v1/admin/auth-audit?limit=500", token=admin_b)
check("another facility's audit log does not show it",
      status == 200 and not any(e["deviceId"] == LOST for e in body))

sys.exit(t.finish())
