"""Dashboard tokens work on the admin API but are refused by the Sync Gateway."""
import secrets
import sys

from client import Checks, create_clinician, http, login, sql

FAC = "fac-001"
DEVICE = "dev-a41c"
PASSWORD = "Dash-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
ADMIN, NURSE = f"admin.dash.{suffix}", f"nurse.dash.{suffix}"

t = Checks()
check = t.check


def dashboard_login(username, password, **extra):
    return http("POST", "/v1/auth/login",
                {"username": username, "password": password, "clientId": "admin-dashboard", **extra})


print("Setup")
create_clinician(ADMIN, PASSWORD, "admin", FAC, "Dashboard Admin")
_, body = login(ADMIN, PASSWORD, DEVICE)
admin_mobile = body["accessToken"]
status, _ = http("POST", "/v1/admin/clinicians",
                 {"username": NURSE, "password": PASSWORD, "fullName": "Dashboard Nurse", "role": "nurse"}, admin_mobile)
check("nurse registered", status == 201, f"{status}")

print("Login rules")
status, body = dashboard_login(ADMIN, PASSWORD)
check("admin logs in to the dashboard without a device", status == 200 and "accessToken" in body, f"{status} {body}")
dash_token, dash_refresh = body["accessToken"], body["refreshToken"]
status, body = dashboard_login(NURSE, PASSWORD)
check("a nurse cannot use the dashboard", status == 401 and body["code"] == "CLIENT_NOT_ALLOWED", f"{status} {body}")
status, body = dashboard_login(NURSE, "wrong-password")
check("... and a wrong password still says INVALID_CREDENTIALS (role not revealed)",
      status == 401 and body["code"] == "INVALID_CREDENTIALS", f"{status} {body}")
status, body = dashboard_login(ADMIN, PASSWORD, deviceId=DEVICE)
check("a dashboard login with a device is refused", status == 400 and body["code"] == "DEVICE_NOT_EXPECTED", f"{status} {body}")
status, body = http("POST", "/v1/auth/login", {"username": ADMIN, "password": PASSWORD, "clientId": "something"})
check("an unknown client is refused", status == 400 and body["code"] == "UNKNOWN_CLIENT", f"{status} {body}")
status, body = http("POST", "/v1/auth/login", {"username": ADMIN, "password": PASSWORD})
check("a mobile login still needs a device", status == 400 and body["code"] == "MISSING_FIELDS", f"{status} {body}")

print("Session row")
row = sql(f"""select s.client_id, s.device_id is null, s.expires_at < now() + interval '13 hours'
              from clinical.clinician_session s join clinical.clinician c using (clinician_id)
              where c.username = '{ADMIN}' and s.revoked_at is null and s.client_id = 'admin-dashboard'""")
check("stored as an admin-dashboard session with no device and a 12-hour lifetime", row == "admin-dashboard|t|t", row)
status = sql("""select count(*) from clinical.clinician_session where client_id = 'mobile' and device_id is null""")
check("no mobile session exists without a device", status == "0", status)

print("What a dashboard token can reach")
status, _ = http("GET", "/v1/admin/clinicians", token=dash_token)
check("admin API: allowed", status == 200, f"{status}")
status, _ = http("GET", "/v1/sync/changes?cursor=0", token=dash_token)
check("pull: refused by the Sync Gateway", status == 401, f"{status}")
status, _ = http("POST", "/v1/sync/push", {"deviceId": DEVICE, "events": []}, dash_token)
check("push: refused by the Sync Gateway", status == 401, f"{status}")
status, _ = http("GET", "/v1/patients/p-0000000", token=dash_token)
check("patient records: refused by the Sync Gateway", status == 401, f"{status}")

print("Refresh and logout")
status, body = http("POST", "/v1/auth/refresh", {"refreshToken": dash_refresh})
check("refresh works", status == 200, f"{status} {body}")
new_token, new_refresh = body["accessToken"], body["refreshToken"]
status, _ = http("GET", "/v1/sync/changes?cursor=0", token=new_token)
check("the refreshed token is still a dashboard token", status == 401, f"{status}")
status, _ = http("GET", "/v1/admin/clinicians", token=new_token)
check("... and still reaches the admin API", status == 200, f"{status}")
status, _ = http("POST", "/v1/auth/logout", {"refreshToken": new_refresh})
status2, _ = http("POST", "/v1/auth/refresh", {"refreshToken": new_refresh})
check("logout revokes the dashboard session", status == 204 and status2 == 401, f"{status} {status2}")

sys.exit(t.finish())
