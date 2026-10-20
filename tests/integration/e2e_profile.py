"""Your own account through /v1/me (profile management plan), from start to finish. Safe to run again."""
import secrets
import sys
from datetime import datetime

from client import Checks, create_clinician, dashboard_login, http, login, refresh, sql

FAC_A, FAC_B = "fac-001", "fac-002"
PASSWORD = "Profile-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
ADMIN, NURSE, OTHER = f"prof.admin.{suffix}", f"prof.nurse.{suffix}", f"prof.other.{suffix}"
PW_USER, LOCK_USER, DUE_USER = f"prof.pw.{suffix}", f"prof.lock.{suffix}", f"prof.due.{suffix}"
SESS_USER = f"prof.sess.{suffix}"
NEW_PASSWORD = "Profile-New-" + secrets.token_hex(4)
DEVICE = "dev-prof-" + secrets.token_hex(3)

t = Checks()
check = t.check


def stamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")) if value else None


print("Setup")
sql(f"INSERT INTO clinical.facility (facility_id, name) VALUES ('{FAC_B}', 'Second Test Hospital') ON CONFLICT DO NOTHING")
create_clinician(ADMIN, PASSWORD, "admin", FAC_A, "Profile Admin")
create_clinician(OTHER, PASSWORD, "admin", FAC_B, "Other Facility Admin")

print("Read your profile (step 1)")
status, body = dashboard_login(ADMIN, PASSWORD)
dash = body["accessToken"]
status, me = http("GET", "/v1/me", token=dash)
check("the dashboard token reads its own profile through the gateway", status == 200, f"{status} {me}")
check("username, full name, role and facility are the caller's",
      me.get("username") == ADMIN and me.get("fullName") == "Profile Admin" and me.get("role") == "admin"
      and me.get("facilityId") == FAC_A, me)
check("two-step is off and no password change is due for a command-line account",
      me.get("mfaEnabled") is False and me.get("mustChangePassword") is False, me)
check("created and last sign-in times are filled in", stamp(me.get("createdAt")) and stamp(me.get("lastSignInAt")), me)
check("nothing secret comes back", not ({"passwordHash", "passwordSalt", "mfaSecret", "clinicianId"} & set(me)), list(me))

first_sign_in = stamp(me["lastSignInAt"])
dashboard_login(ADMIN, PASSWORD)
status, again = http("GET", "/v1/me", token=dash)
check("last sign-in moves forward with the next sign-in", stamp(again.get("lastSignInAt")) >= first_sign_in, again)

status, body = http("POST", "/v1/admin/clinicians",
                    {"username": NURSE, "password": PASSWORD, "fullName": "Profile Nurse", "role": "nurse"}, dash)
check("admin registers a nurse", status == 201, f"{status} {body}")
status, body = login(NURSE, PASSWORD, DEVICE)
nurse = body.get("accessToken")
status, nurse_me = http("GET", "/v1/me", token=nurse)
check("a nurse's mobile token reads the nurse's own profile",
      status == 200 and nurse_me.get("username") == NURSE and nurse_me.get("role") == "nurse", f"{status} {nurse_me}")
check("an admin-chosen password shows as due for change", nurse_me.get("mustChangePassword") is True, nurse_me)

_, body = login(OTHER, PASSWORD, DEVICE + "-b")
status, other_me = http("GET", "/v1/me", token=body["accessToken"])
check("another facility's admin sees only their own account",
      status == 200 and other_me.get("username") == OTHER and other_me.get("facilityId") == FAC_B, other_me)

status, _ = http("GET", "/v1/me")
check("no token is 401", status == 401, status)
status, _ = http("GET", "/v1/me", token=dash[:-4] + "AAAA")
check("a tampered token is 401", status == 401, status)

print("Change your name (step 3)")
status, body = http("PATCH", "/v1/me", {"fullName": "  Profile Admin Renamed  "}, dash)
check("PATCH /v1/me saves the new name, trimmed, and answers the updated profile",
      status == 200 and body.get("fullName") == "Profile Admin Renamed" and body.get("username") == ADMIN,
      f"{status} {body}")
check("... and it is stored",
      sql(f"select full_name from clinical.clinician where username = '{ADMIN}'") == "Profile Admin Renamed")
status, listing = http("GET", "/v1/admin/clinicians", token=dash)
check("other admins see the new name in the clinicians list",
      any(c["username"] == ADMIN and c["fullName"] == "Profile Admin Renamed" for c in listing or []), status)
audit = f"select count(*) from audit.auth_audit a where a.action = 'PROFILE_UPDATE' and a.username = '{ADMIN}'"
check("the change is audited as PROFILE_UPDATE, with you as the actor",
      sql(audit) == "1"
      and sql(f"{audit} and a.actor_clinician_id = a.clinician_id and a.success") == "1")

status, body = http("PATCH", "/v1/me", {"fullName": "Profile Admin Renamed"}, dash)
check("the same name again is fine and not audited twice", status == 200 and sql(audit) == "1", f"{status} {body}")
status, body = http("PATCH", "/v1/me", {"fullName": "   "}, dash)
check("an empty name is refused", status == 400 and body.get("code") == "MISSING_FULL_NAME", f"{status} {body}")
status, body = http("PATCH", "/v1/me", {}, dash)
check("a missing name is refused", status == 400 and body.get("code") == "MISSING_FULL_NAME", f"{status} {body}")
status, body = http("PATCH", "/v1/me", {"fullName": "x" * 121}, dash)
check("a name over 120 characters is refused", status == 400 and body.get("code") == "FULL_NAME_TOO_LONG",
      f"{status} {body}")
status, body = http("PATCH", "/v1/me", {"fullName": "x" * 120}, dash)
check("exactly 120 characters is accepted", status == 200, f"{status} {body}")

status, body = http("PATCH", "/v1/me", {"fullName": "Hijacked", "username": OTHER, "role": "admin",
                                         "facilityId": FAC_B}, nurse)
check("extra fields (username, role, facility) are ignored: only the caller's own name changes",
      status == 200 and body.get("username") == NURSE and body.get("role") == "nurse"
      and body.get("facilityId") == FAC_A, f"{status} {body}")
check("... and the other account is untouched",
      sql(f"select full_name from clinical.clinician where username = '{OTHER}'") == "Other Facility Admin")
status, _ = http("PATCH", "/v1/me", {"fullName": "Nobody"})
check("renaming without a token is 401", status == 401, status)

print("Change your password (step 5)")
create_clinician(PW_USER, PASSWORD, "admin", FAC_A, "Password Changer")
_, body = dashboard_login(PW_USER, PASSWORD)
pw_dash, pw_dash_refresh = body["accessToken"], body["refreshToken"]
_, body = login(PW_USER, PASSWORD, DEVICE + "-pw")
pw_phone_refresh = body["refreshToken"]

status, body = http("POST", "/v1/me/password", {"currentPassword": "not-my-password", "newPassword": NEW_PASSWORD}, pw_dash)
check("a wrong current password is refused", status == 400 and body.get("code") == "INVALID_CREDENTIALS", f"{status} {body}")
check("... counts as a failed attempt",
      sql(f"select cc.failed_attempts from clinical.clinician_credential cc join clinical.clinician c using (clinician_id) "
          f"where c.username = '{PW_USER}'") == "1")
status, body = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": "short"}, pw_dash)
check("a new password under 8 characters is refused", status == 400 and body.get("code") == "PASSWORD_TOO_SHORT",
      f"{status} {body}")
status, body = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": PASSWORD}, pw_dash)
check("the same password again is refused", status == 400 and body.get("code") == "PASSWORD_UNCHANGED", f"{status} {body}")
status, body = http("POST", "/v1/me/password", {"currentPassword": PASSWORD}, pw_dash)
check("a missing new password is refused", status == 400 and body.get("code") == "PASSWORD_TOO_SHORT", f"{status} {body}")
status, _ = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": NEW_PASSWORD})
check("without a token it is 401", status == 401, status)

status, body = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": NEW_PASSWORD}, pw_dash)
check("the right current password and a new one: 204", status == 204, f"{status} {body}")
status, body = dashboard_login(PW_USER, PASSWORD)
check("the old password no longer signs in", status == 400 and body.get("code") == "INVALID_CREDENTIALS", f"{status} {body}")
status, _ = dashboard_login(PW_USER, NEW_PASSWORD)
check("the new password signs in", status == 200, status)
status, _ = refresh(pw_dash_refresh, client_id="admin-dashboard")
check("every session ended: the dashboard's refresh is refused", status == 400, status)
status, _ = refresh(pw_phone_refresh)
check("... and the phone's too", status == 400, status)
check("the failed-attempt count is back to 0",
      sql(f"select cc.failed_attempts from clinical.clinician_credential cc join clinical.clinician c using (clinician_id) "
          f"where c.username = '{PW_USER}'") == "0")
changes = f"select count(*) from audit.auth_audit a where a.action = 'PASSWORD_CHANGE' and a.username = '{PW_USER}'"
check("audited: one failed and one successful PASSWORD_CHANGE, both with you as actor",
      sql(f"{changes} and a.success and a.actor_clinician_id = a.clinician_id") == "1"
      and sql(f"{changes} and not a.success and a.reason_code = 'INVALID_CREDENTIALS'") == "1")

print("Wrong current passwords lock the account (step 5)")
create_clinician(LOCK_USER, PASSWORD, "admin", FAC_A, "Lock Test")
_, body = login(LOCK_USER, PASSWORD, DEVICE + "-lock")
lock_token = body["accessToken"]
for _ in range(4):
    http("POST", "/v1/me/password", {"currentPassword": "wrong-password", "newPassword": NEW_PASSWORD}, lock_token)
status, body = http("POST", "/v1/me/password", {"currentPassword": "wrong-password", "newPassword": NEW_PASSWORD},
                    lock_token)
check("the 5th wrong current password locks the account (409)", status == 409 and body.get("code") == "CREDENTIAL_LOCKED",
      f"{status} {body}")
check("... recorded as a LOCKOUT",
      sql(f"select count(*) from audit.auth_audit where action = 'LOCKOUT' and username = '{LOCK_USER}'") == "1")
status, body = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": NEW_PASSWORD}, lock_token)
check("while locked even the right current password is refused", status == 409 and body.get("code") == "CREDENTIAL_LOCKED",
      f"{status} {body}")
status, body = login(LOCK_USER, PASSWORD, DEVICE + "-lock")
check("... and signing in is locked too", status == 400 and body.get("code") == "CREDENTIAL_LOCKED", f"{status} {body}")

print("Changing your own password clears 'choose your own' (step 5)")
http("POST", "/v1/admin/clinicians",
     {"username": DUE_USER, "password": PASSWORD, "fullName": "Due Nurse", "role": "nurse"}, dash)
_, body = login(DUE_USER, PASSWORD, DEVICE + "-due")
due = body["accessToken"]
check("an admin-chosen password is due for change", http("GET", "/v1/me", token=due)[1].get("mustChangePassword") is True)
status, _ = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": NEW_PASSWORD}, due)
check("changing it yourself clears that", status == 204 and http("GET", "/v1/me", token=due)[1].get("mustChangePassword") is False,
      status)

print("Your sessions (step 7)")
create_clinician(SESS_USER, PASSWORD, "admin", FAC_A, "Session Owner")
_, browser_1 = dashboard_login(SESS_USER, PASSWORD)
_, browser_2 = dashboard_login(SESS_USER, PASSWORD)
_, phone = login(SESS_USER, PASSWORD, DEVICE + "-sess")
token = browser_1["accessToken"]
status, _ = refresh(browser_2["refreshToken"], client_id="admin-dashboard")
status, sessions = http("GET", "/v1/me/sessions", token=token)
check("lists your three sign-ins: two browsers and a phone (a refresh stays the same session)",
      status == 200 and len(sessions) == 3, f"{status} {sessions}")
check("each has a client, and only the phone has a device",
      sorted(x["client"] for x in sessions) == ["admin-dashboard", "admin-dashboard", "mobile"]
      and [x["deviceId"] for x in sessions if x["client"] == "mobile"] == [DEVICE + "-sess"]
      and all(x["deviceId"] is None for x in sessions if x["client"] != "mobile"), sessions)
check("signed in, last used and expiry are in order",
      all(stamp(x["signedInAt"]) <= stamp(x["lastUsedAt"]) < stamp(x["expiresAt"]) for x in sessions), sessions)
check("most recently used first", [stamp(x["lastUsedAt"]) for x in sessions]
      == sorted((stamp(x["lastUsedAt"]) for x in sessions), reverse=True), sessions)
check("nothing secret in the list", not any({"refreshTokenHash", "clinicianId"} & set(x) for x in sessions), sessions)

status, others = http("GET", "/v1/me/sessions", token=dash)
check("another admin's list never shows your sessions",
      status == 200 and not {x["sessionId"] for x in others} & {x["sessionId"] for x in sessions}, status)

phone_session = next(x["sessionId"] for x in sessions if x["client"] == "mobile")
status, body = http("DELETE", f"/v1/me/sessions/{phone_session}", token=token)
check("ending the phone's session: 204", status == 204, f"{status} {body}")
status, _ = refresh(phone["refreshToken"])
check("... its refresh token stops working", status == 400, status)
status, left = http("GET", "/v1/me/sessions", token=token)
check("... and it leaves the list", status == 200 and len(left) == 2 and phone_session not in {x["sessionId"] for x in left},
      left)
check("... audited as LOGOUT for that device, with you as actor",
      sql(f"select count(*) from audit.auth_audit a where a.action = 'LOGOUT' and a.username = '{SESS_USER}' "
          f"and a.device_id = '{DEVICE}-sess' and a.actor_clinician_id = a.clinician_id") == "1")
status, _ = http("DELETE", f"/v1/me/sessions/{phone_session}", token=token)
check("ending it again is 404", status == 404, status)
status, _ = http("DELETE", "/v1/me/sessions/7d0e8d4e-3c1a-4c55-9a48-000000000000", token=token)
check("an unknown session is 404", status == 404, status)
status, _ = http("DELETE", "/v1/me/sessions/not-a-session-id", token=token)
check("a malformed id is 404", status == 404, status)
other_session = others[0]["sessionId"] if others else "00000000-0000-0000-0000-000000000000"
status, _ = http("DELETE", f"/v1/me/sessions/{other_session}", token=token)
check("someone else's session is 404, never ended", status == 404 and http("GET", "/v1/me/sessions", token=dash)[1]
      and any(x["sessionId"] == other_session for x in http("GET", "/v1/me/sessions", token=dash)[1]), status)

status, _ = http("DELETE", "/v1/me/sessions", token=token)
check("ending all your sessions: 204", status == 204, status)
status, _ = refresh(browser_1["refreshToken"], client_id="admin-dashboard")
check("... both browsers' refresh tokens stop working", status == 400
      and refresh(browser_2["refreshToken"], client_id="admin-dashboard")[0] == 400, status)
status, left = http("GET", "/v1/me/sessions", token=token)
check("... and the list is empty", status == 200 and left == [], left)
status, _ = http("GET", "/v1/me/sessions")
check("without a token it is 401", status == 401, status)

sql(f"update clinical.clinician set active = false where username = '{NURSE}'")
status, body = http("GET", "/v1/me", token=nurse)
check("a deactivated account's still-valid token gets 404, not the profile", status == 404, f"{status} {body}")
status, body = http("PATCH", "/v1/me", {"fullName": "Too Late"}, nurse)
check("... and cannot rename the account either", status == 404, f"{status} {body}")
status, body = http("POST", "/v1/me/password", {"currentPassword": PASSWORD, "newPassword": NEW_PASSWORD}, nurse)
check("... or change its password", status == 404, f"{status} {body}")

sys.exit(t.finish())
