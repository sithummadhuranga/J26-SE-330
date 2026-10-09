"""Drives known login traffic and checks the auth-health metrics and Prometheus series (run while nobody else logs in)."""
import json
import os
import secrets
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone

from client import Checks, create_clinician, http, login, sql, totp_code

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "evaluation"))
import metrics  # noqa: E402

PROMETHEUS = os.environ.get("PROMETHEUS", "http://localhost:9090")
suffix = secrets.token_hex(3)
PASSWORD = "AuthHealth-" + secrets.token_hex(4)
NURSE, LOCKED, MFA_USER = f"nurse.ah.{suffix}", f"locked.ah.{suffix}", f"mfa.ah.{suffix}"
DEVICE = f"dev-ah-{suffix}"

t = Checks()
check = t.check


def prom(query):
    try:
        url = f"{PROMETHEUS}/api/v1/query?query={urllib.parse.quote(query)}"
        with urllib.request.urlopen(url, timeout=5) as r:
            result = json.load(r)["data"]["result"]
        return sum(float(s["value"][1]) for s in result) if result else 0.0
    except OSError:
        return None


print("Setup")
for user in (NURSE, LOCKED, MFA_USER):
    create_clinician(user, PASSWORD, "nurse", "fac-001", "Auth Health")
_, body = login(MFA_USER, PASSWORD, DEVICE)
status, enrol = http("POST", "/v1/auth/mfa/enroll", token=body["accessToken"])
http("POST", "/v1/auth/mfa/confirm", {"code": totp_code(enrol["secret"])}, body["accessToken"])
lockouts_before = prom('sum(auth_events_total{action="LOCKOUT"})')
time.sleep(1.1)

print("Known traffic: 2 logins, 5 failed attempts (the 5th locks), 1 MFA prompt, 1 refresh, 1 logout")
started = datetime.now(timezone.utc)
_, first = login(NURSE, PASSWORD, DEVICE)
time.sleep(1.5)
status, refreshed = http("POST", "/v1/auth/refresh", {"refreshToken": first["refreshToken"]})
check("refresh works", status == 200, status)
time.sleep(1.5)
status, _ = http("POST", "/v1/auth/logout", {"refreshToken": refreshed["refreshToken"]})
check("logout works", status in (200, 204), status)
_, second = login(NURSE, PASSWORD, DEVICE)
for _ in range(5):
    login(LOCKED, "wrong-password", DEVICE)
status, body = login(MFA_USER, PASSWORD, DEVICE)
check("the MFA user is asked for a code", status == 401 and body["code"] == "MFA_REQUIRED", f"{status} {body}")
time.sleep(1.1)
finished = datetime.now(timezone.utc)

print("\nSession families")
families = sql(f"""select count(distinct s.family_id) || '/' || count(*) from clinical.clinician_session s
                   join clinical.clinician c using (clinician_id) where c.username = '{NURSE}'""")
check("the refresh stayed in its login's session; the second login started another (2 families, 3 rows)",
      families == "2/3", families)

print("\nmetrics.auth_health for the window")
a = metrics.auth_health(started, finished)
check("2 successful logins", a["logins"] == 2, a)
check("5 failed attempts, including the one that locked", a["login_failures"] == 5, a)
check("1 lockout", a["lockouts"] == 1, a)
check("the MFA prompt is not a failed attempt", a["mfa_prompts"] == 1 and "MFA_REQUIRED" not in a["failures_by_reason"], a)
check("login failure rate is 5 / 7", a["login_failure_rate"] == round(5 / 7, 4), a["login_failure_rate"])
check("failures by reason", a["failures_by_reason"] == {"INVALID_CREDENTIALS": 4, "CREDENTIAL_LOCKED": 1},
      a["failures_by_reason"])
check("lockouts per day are scaled from the window", a["lockouts_per_day"] > 1000, a["lockouts_per_day"])
s = a["sessions"]
check("2 sessions started, 1 ended (logout), 1 still active", (s["started"], s["ended"], s["active_at_end"]) == (2, 1, 1), s)
check("the ended session lived from login to logout, across its refresh (about 3 s)",
      s["mean_lifetime_s"] is not None and 2.5 <= s["mean_lifetime_s"] <= 10, s)

print("\nPrometheus (identity-service metrics for the dashboard)")
if lockouts_before is None:
    print("  SKIP  Prometheus not reachable (start the tools profile)")
else:
    deadline, after = time.time() + 30, lockouts_before
    while time.time() < deadline and after < lockouts_before + 1:
        time.sleep(3)
        after = prom('sum(auth_events_total{action="LOCKOUT"})')
    check("the lockout reached auth_events_total", after >= lockouts_before + 1, (lockouts_before, after))
    check("failed logins are labelled by reason",
          prom('sum(auth_events_total{action="LOGIN",success="false",reason="INVALID_CREDENTIALS"})') >= 4)
    deadline, active = time.time() + 90, 0
    while time.time() < deadline and not active:  # sampled once a minute
        active = prom('sum(max by (client) (auth_sessions_active{client="mobile"}))')
        if not active:
            time.sleep(5)
    check("auth_sessions_active is sampled", active and active >= 1, active)
    check("auth_session_lifetime_seconds is exported for active sessions",
          (prom('max(auth_session_lifetime_seconds{state="active",client="mobile"})') or 0) > 0)

sys.exit(t.finish())
