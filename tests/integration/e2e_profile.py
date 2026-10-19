"""Your own account through /v1/me (plan: profile management), end to end; safe to re-run."""
import secrets
import sys
from datetime import datetime

from client import Checks, create_clinician, dashboard_login, http, login, sql

FAC_A, FAC_B = "fac-001", "fac-002"
PASSWORD = "Profile-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
ADMIN, NURSE, OTHER = f"prof.admin.{suffix}", f"prof.nurse.{suffix}", f"prof.other.{suffix}"
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

sql(f"update clinical.clinician set active = false where username = '{NURSE}'")
status, body = http("GET", "/v1/me", token=nurse)
check("a deactivated account's still-valid token gets 404, not the profile", status == 404, f"{status} {body}")
status, body = http("PATCH", "/v1/me", {"fullName": "Too Late"}, nurse)
check("... and cannot rename the account either", status == 404, f"{status} {body}")

sys.exit(t.finish())
