"""Clinician registration, login, MFA, admin actions and the auth audit trail end to end; safe to re-run."""
import secrets
import sys
import time

from client import Checks, create_clinician, http, login, sql, totp_code, uuid7, wait_for, wound_event

FAC_A, FAC_B = "fac-001", "fac-002"
DEVICE_A, DEVICE_B = "dev-a41c", "dev-b-" + secrets.token_hex(3)
PASSWORD = "Step2-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
ADMIN_A, ADMIN_B, NURSE = f"admin.a.{suffix}", f"admin.b.{suffix}", f"nurse.{suffix}"

t = Checks()
check = t.check

print("Setup: second facility and one bootstrap admin per facility (command line)")
sql(f"INSERT INTO clinical.facility (facility_id, name) VALUES ('{FAC_B}', 'Second Test Hospital') ON CONFLICT DO NOTHING")
create_clinician(ADMIN_A, PASSWORD, "admin", FAC_A, "Admin A")
create_clinician(ADMIN_B, PASSWORD, "admin", FAC_B, "Admin B")
_, body = login(ADMIN_A, PASSWORD, DEVICE_A)
admin_a = body["accessToken"]
_, body = login(ADMIN_B, PASSWORD, DEVICE_B)
admin_b = body["accessToken"]
check("bootstrap registration is audited",
      sql(f"select count(*) from audit.auth_audit where action='REGISTER' and username='{ADMIN_A}'") == "1")

print("Registration by an admin")
status, body = http("POST", "/v1/admin/clinicians",
                    {"username": NURSE, "password": PASSWORD, "fullName": "Test Nurse", "role": "nurse"}, admin_a)
check("admin registers a nurse (201)", status == 201 and body.get("clinicianId"), f"{status} {body}")
actor = sql(f"""select actor.username from audit.auth_audit a join clinical.clinician actor
                on actor.clinician_id = a.actor_clinician_id where a.action='REGISTER' and a.username='{NURSE}'""")
check("REGISTER audit row names the admin as actor", actor == ADMIN_A, actor)
check("nurse is placed in the admin's facility",
      sql(f"select facility_id from clinical.clinician where username='{NURSE}'") == FAC_A)

status, body = http("POST", "/v1/admin/clinicians",
                    {"username": NURSE, "password": PASSWORD, "fullName": "Again", "role": "nurse"}, admin_a)
check("duplicate username is 409", status == 409 and body["code"] == "USERNAME_TAKEN", f"{status} {body}")
status, body = http("POST", "/v1/admin/clinicians",
                    {"username": "short.pw." + suffix, "password": "short", "fullName": "X", "role": "nurse"}, admin_a)
check("short password is 400", status == 400 and body["code"] == "PASSWORD_TOO_SHORT", f"{status} {body}")
status, body = http("POST", "/v1/admin/clinicians",
                    {"username": "bad.role." + suffix, "password": PASSWORD, "fullName": "X", "role": "surgeon"}, admin_a)
check("unknown role is 400", status == 400 and body["code"] == "INVALID_ROLE", f"{status} {body}")

status, body = login(NURSE, PASSWORD, DEVICE_A)
check("registered nurse can log in", status == 200, f"{status} {body}")
nurse = body["accessToken"]
status, _ = http("POST", "/v1/admin/clinicians",
                 {"username": "x." + suffix, "password": PASSWORD, "fullName": "X", "role": "nurse"}, nurse)
check("a nurse cannot register clinicians (403)", status == 403, f"{status}")

status, body = http("GET", "/v1/admin/clinicians", token=admin_a)
check("admin list shows the nurse", status == 200 and any(c["username"] == NURSE for c in body), f"{status}")
status, body = http("GET", "/v1/admin/clinicians", token=admin_b)
check("other facility's admin does not see the nurse", status == 200 and not any(c["username"] == NURSE for c in body))
status, _ = http("POST", f"/v1/admin/clinicians/{NURSE}/unlock", token=admin_b)
check("other facility's admin gets 404, not 403", status == 404, f"{status}")

print("TOTP multi-factor login")
status, body = http("POST", "/v1/auth/mfa/enroll", token=nurse)
check("enroll returns a secret and otpauth URI", status == 200 and body["otpauthUri"].startswith("otpauth://totp/"), f"{status} {body}")
secret = body["secret"]
status, _ = login(NURSE, PASSWORD, DEVICE_A)
check("MFA is not required until confirmed", status == 200, f"{status}")
status, body = http("POST", "/v1/auth/mfa/confirm", {"code": "000000"}, nurse)
check("wrong confirmation code is 400", status == 400 and body["code"] == "INVALID_TOTP", f"{status} {body}")
step = int(time.time()) // 30
status, _ = http("POST", "/v1/auth/mfa/confirm", {"code": totp_code(secret, step)}, nurse)
check("correct code enables MFA (204)", status == 204, f"{status}")
check("stored secret is encrypted, not plain", sql(
    f"""select length(cc.mfa_secret_encrypted) > 20 from clinical.clinician_credential cc
        join clinical.clinician c using (clinician_id) where c.username='{NURSE}'""") == "t")

status, body = login(NURSE, PASSWORD, DEVICE_A)
check("login without a code now asks for one", status == 401 and body["code"] == "MFA_REQUIRED", f"{status} {body}")
status, body = login(NURSE, PASSWORD, DEVICE_A, totp_code(secret, step))
check("the code used to confirm cannot be replayed", status == 401 and body["code"] == "INVALID_TOTP", f"{status} {body}")
status, body = login(NURSE, PASSWORD, DEVICE_A, totp_code(secret, step + 1))
check("next valid code logs in", status == 200, f"{status} {body}")
nurse = body["accessToken"] if status == 200 else nurse
status, body = login(NURSE, PASSWORD, DEVICE_A, totp_code(secret, step + 1))
check("that code cannot be used twice", status == 401 and body["code"] == "INVALID_TOTP", f"{status} {body}")
status, body = http("POST", "/v1/auth/mfa/enroll", token=nurse)
check("re-enrolling while enabled is 409", status == 409 and body["code"] == "MFA_ALREADY_ENABLED", f"{status} {body}")

status, _ = http("POST", f"/v1/admin/clinicians/{NURSE}/reset-mfa", token=admin_a)
check("admin resets MFA (lost phone)", status == 204, f"{status}")
status, body = login(NURSE, PASSWORD, DEVICE_A)
check("after reset, password alone works again", status == 200, f"{status} {body}")
nurse, nurse_refresh = body["accessToken"], body["refreshToken"]

print("Patient display alias (never taken from Kafka)")
patient = "p-" + secrets.token_hex(4)
status, body = http("PUT", f"/v1/patients/{patient}/alias", {"displayAlias": "Bed 12 / K.P."}, nurse)
check("nurse sets an alias", status == 200 and body["displayAlias"] == "Bed 12 / K.P.", f"{status} {body}")
status, body = http("GET", f"/v1/patients/{patient}", token=nurse)
check("alias is readable in the same facility", status == 200 and body["displayAlias"] == "Bed 12 / K.P.", f"{status} {body}")
status, _ = http("GET", f"/v1/patients/{patient}", token=admin_b)
check("another facility cannot read it (404)", status == 404, f"{status}")
status, _ = http("PUT", f"/v1/patients/{patient}/alias", {"displayAlias": "hijack"}, admin_b)
check("another facility cannot change it (404)", status == 404, f"{status}")
status, _ = http("PUT", "/v1/patients/Kamal-Perera/alias", {"displayAlias": "x"}, nurse)
check("a name instead of a pseudonym is rejected (400)", status == 400, f"{status}")

evt = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A, patientRef=patient)
status, body = http("POST", "/v1/sync/push", {"deviceId": DEVICE_A, "events": [evt]}, nurse)
check("assessment for that patient is accepted", status == 200 and body["results"][0]["status"] == "ACCEPTED", f"{status} {body}")
check("... and persisted", wait_for(
    f"select count(*) from clinical.wound_assessment where event_id='{evt['eventId']}'", "1"))
status, body = http("GET", f"/v1/patients/{patient}", token=nurse)
check("persister did not overwrite the alias", status == 200 and body["displayAlias"] == "Bed 12 / K.P." and body["woundCount"] == 1,
      f"{status} {body}")

print("Lockout, unlock and deactivation")
for _ in range(5):
    login(NURSE, "wrong-password", DEVICE_A)
status, body = login(NURSE, PASSWORD, DEVICE_A)
check("5 failures lock the account", status == 401 and body["code"] == "CREDENTIAL_LOCKED", f"{status} {body}")
status, _ = http("POST", f"/v1/admin/clinicians/{NURSE}/unlock", token=admin_a)
check("admin unlocks (204)", status == 204, f"{status}")
status, _ = login(NURSE, PASSWORD, DEVICE_A)
check("login works after unlock", status == 200, f"{status}")

status, body = http("POST", f"/v1/admin/clinicians/{ADMIN_A}/deactivate", token=admin_a)
check("admin cannot deactivate themselves", status == 409 and body["code"] == "CANNOT_DEACTIVATE_SELF", f"{status} {body}")
status, _ = http("POST", f"/v1/admin/clinicians/{NURSE}/deactivate", token=admin_a)
check("admin deactivates the nurse (204)", status == 204, f"{status}")
status, _ = login(NURSE, PASSWORD, DEVICE_A)
check("deactivated nurse cannot log in", status == 401, f"{status}")
status, _ = http("POST", "/v1/auth/refresh", {"refreshToken": nurse_refresh})
check("... and cannot refresh an existing session", status == 401, f"{status}")

print("Audit trail")
status, body = http("GET", "/v1/admin/auth-audit?limit=500", token=admin_a)
actions = {e["action"] for e in body if e["username"] == NURSE} if status == 200 else set()
expected = {"REGISTER", "LOGIN", "MFA_ENROLL", "MFA_CONFIRM", "MFA_RESET", "LOCKOUT", "UNLOCK", "DEACTIVATE"}
check("admin audit log shows the nurse's whole lifecycle", expected <= actions, f"missing {expected - actions}")
by_admin = {e["action"] for e in body if e["username"] == NURSE and e["actorUsername"] == ADMIN_A}
check("admin actions carry the admin as actor", {"REGISTER", "MFA_RESET", "UNLOCK", "DEACTIVATE"} <= by_admin, by_admin)
failed = [e for e in body if e["username"] == NURSE and not e["success"]]
check("failed attempts are recorded with a reason",
      {"MFA_REQUIRED", "INVALID_TOTP", "INVALID_CREDENTIALS"} <= {e["reasonCode"] for e in failed})
status, body = http("GET", "/v1/admin/auth-audit?limit=500", token=admin_b)
check("other facility's audit log does not include the nurse",
      status == 200 and not any(e["username"] == NURSE for e in body))
status, _ = http("GET", "/v1/admin/auth-audit", token=nurse)
check("non-admins cannot read the audit log", status in (401, 403), f"{status}")

sys.exit(t.finish())
