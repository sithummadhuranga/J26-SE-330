"""Dashboard tokens work on the admin API but are refused by the Sync Gateway."""
import secrets
import sys

from client import (DASHBOARD_REDIRECT, Browser, Checks, authorize_url, create_clinician, dashboard_login, http, login,
                    logout, pkce, refresh, sql, token_request)

FAC = "fac-001"
DEVICE = "dev-a41c"
PASSWORD = "Dash-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
ADMIN, NURSE = f"admin.dash.{suffix}", f"nurse.dash.{suffix}"

t = Checks()
check = t.check

print("Setup")
create_clinician(ADMIN, PASSWORD, "admin", FAC, "Dashboard Admin")
_, body = login(ADMIN, PASSWORD, DEVICE)
admin_mobile = body["accessToken"]
status, _ = http("POST", "/v1/admin/clinicians",
                 {"username": NURSE, "password": PASSWORD, "fullName": "Dashboard Nurse", "role": "nurse"}, admin_mobile)
check("nurse registered", status == 201, f"{status}")

print("Login rules")
status, body = dashboard_login(ADMIN, PASSWORD)
check("admin signs in to the dashboard without a device", status == 200 and "accessToken" in body, f"{status} {body}")
dash_token, dash_refresh = body["accessToken"], body["refreshToken"]
status, body = dashboard_login(NURSE, PASSWORD)
check("a nurse cannot use the dashboard", status == 400 and body["code"] == "CLIENT_NOT_ALLOWED", f"{status} {body}")
status, body = dashboard_login(NURSE, "wrong-password")
check("... and a wrong password still says INVALID_CREDENTIALS (role not revealed)",
      status == 400 and body["code"] == "INVALID_CREDENTIALS", f"{status} {body}")
status, _, _, out = Browser().follow(authorize_url("admin-dashboard", DASHBOARD_REDIRECT, pkce()[1], device=DEVICE))
check("a dashboard sign-in with a device is refused", out is None and status == 400, f"{status} {out}")
status, body = token_request("/connect/token", {"grant_type": "password", "client_id": "something",
                                                "username": ADMIN, "password": PASSWORD})
check("an unknown client is refused", status in (400, 401) and body["error"] == "invalid_client", f"{status} {body}")
status, body = token_request("/connect/token", {"grant_type": "password", "client_id": "mobile",
                                                "username": ADMIN, "password": PASSWORD})
check("a mobile sign-in still needs a device", status == 400 and body["error"] == "invalid_request", f"{status} {body}")

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
status, body = refresh(dash_refresh, "admin-dashboard")
check("refresh works", status == 200, f"{status} {body}")
new_token, new_refresh = body["accessToken"], body["refreshToken"]
status, _ = http("GET", "/v1/sync/changes?cursor=0", token=new_token)
check("the refreshed token is still a dashboard token", status == 401, f"{status}")
status, _ = http("GET", "/v1/admin/clinicians", token=new_token)
check("... and still reaches the admin API", status == 200, f"{status}")
status = logout(new_refresh, "admin-dashboard")
status2, _ = refresh(new_refresh, "admin-dashboard")
check("logout revokes the dashboard session", status == 200 and status2 == 400, f"{status} {status2}")

sys.exit(t.finish())
