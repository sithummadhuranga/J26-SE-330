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

sql(f"update clinical.clinician set active = false where username = '{NURSE}'")
status, body = http("GET", "/v1/me", token=nurse)
check("a deactivated account's still-valid token gets 404, not the profile", status == 404, f"{status} {body}")

sys.exit(t.finish())
