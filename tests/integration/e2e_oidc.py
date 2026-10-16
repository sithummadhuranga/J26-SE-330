"""OpenID Connect sign-in end to end (ADR 0006): code + PKCE through the login page, refresh rotation, revocation,
the password grant for tests, and the client rules. Drives the browser flow with plain HTTP; safe to re-run."""
import base64
import json
import secrets
import sys
import time
import urllib.parse
import urllib.request

from client import (DASHBOARD_REDIRECT, GATEWAY, MOBILE_REDIRECT, Browser, Checks, authorize_url, code_from,
                    create_clinician, form_fields, http as api, pkce, sign_in, sql, token_request, totp_code)
from client import page_error as error_code

PASSWORD = "Oidc-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
ADMIN, NURSE = f"oidc.admin.{suffix}", f"oidc.nurse.{suffix}"
DEVICE = "dev-oidc-" + secrets.token_hex(3)

t = Checks()
check = t.check


def token(form):
    status, body = token_request("/connect/token", form)
    return status, body or {}


def claims(jwt):
    part = jwt.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))


def header(jwt):
    part = jwt.split(".")[0]
    return json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))


print("Setup")
create_clinician(ADMIN, PASSWORD, "admin", "fac-001", "OIDC Admin")
create_clinician(NURSE, PASSWORD, "nurse", "fac-001", "OIDC Nurse")

print("Discovery")
status, doc = api("GET", "/.well-known/openid-configuration")
check("discovery answers through the gateway", status == 200, status)
check("issuer is the fixed URL", doc["issuer"] == "https://identity.melanin-wound-cdss/", doc.get("issuer"))
check("endpoints point at the gateway", doc["token_endpoint"] == GATEWAY + "/connect/token", doc.get("token_endpoint"))
check("only S256 PKCE is offered", doc.get("code_challenge_methods_supported") == ["S256"],
      doc.get("code_challenge_methods_supported"))

print("Admin dashboard: authorization code + PKCE")
browser = Browser()
verifier, challenge = pkce()
out, page, code = sign_in(browser, authorize_url("admin-dashboard", DASHBOARD_REDIRECT, challenge), ADMIN, PASSWORD)
check("login page sends the admin back to the dashboard with a code",
      out and out.startswith(DASHBOARD_REDIRECT + "?"), f"{out} {code}")
auth_code = code_from(out)
status, tokens = token({"grant_type": "authorization_code", "client_id": "admin-dashboard", "code": auth_code,
                        "redirect_uri": DASHBOARD_REDIRECT, "code_verifier": verifier})
check("code exchange returns access, refresh and id tokens",
      status == 200 and {"access_token", "refresh_token", "id_token"} <= tokens.keys(), f"{status} {tokens}")
at = claims(tokens["access_token"])
check("access token: dashboard audience, admin role, facility, client_id, no device",
      at.get("aud") == "melanin-wound-cdss-admin" and at.get("role") == "admin" and at.get("facility_id") == "fac-001"
      and at.get("client_id") == "admin-dashboard" and "device_id" not in at, at)
check("access token is a plain RS256 JWT with a kid", header(tokens["access_token"]).get("alg") == "RS256"
      and header(tokens["access_token"]).get("kid"), header(tokens["access_token"]))
check("our private claims (session, code) stay out of the access token", not any(k.startswith("cdss_") for k in at), at)
status, body = api("GET", "/v1/admin/clinicians", token=tokens["access_token"])
check("the dashboard token works on the admin API", status == 200, status)
status, _ = api("GET", "/v1/patients/p-any", token=tokens["access_token"])
check("the dashboard token cannot reach patient data", status in (401, 403), status)

status, again = token({"grant_type": "authorization_code", "client_id": "admin-dashboard", "code": auth_code,
                       "redirect_uri": DASHBOARD_REDIRECT, "code_verifier": verifier})
check("the same code cannot be used twice", status == 400 and again.get("error") == "invalid_grant", f"{status} {again}")

status, rotated = token({"grant_type": "refresh_token", "client_id": "admin-dashboard",
                         "refresh_token": tokens["refresh_token"]})
check("refresh returns new tokens", status == 200 and rotated.get("refresh_token"), f"{status} {rotated}")
status, reused = token({"grant_type": "refresh_token", "client_id": "admin-dashboard",
                        "refresh_token": tokens["refresh_token"]})
check("the old refresh token no longer works", status == 400 and reused.get("error") == "invalid_grant", f"{status} {reused}")
family = sql("select count(distinct family_id) || '/' || count(*) from clinical.clinician_session s "
             f"join clinical.clinician c using (clinician_id) where c.username = '{ADMIN}'")
check("one session family with two rows (login + refresh)", family == "1/2", family)

req = urllib.request.Request(GATEWAY + "/connect/revocation", data=urllib.parse.urlencode(
    {"client_id": "admin-dashboard", "token": rotated["refresh_token"], "token_type_hint": "refresh_token"}).encode())
with urllib.request.urlopen(req, timeout=30) as r:
    check("revocation answers 200", r.status == 200, r.status)
status, body = token({"grant_type": "refresh_token", "client_id": "admin-dashboard",
                      "refresh_token": rotated["refresh_token"]})
check("a revoked refresh token is refused", status == 400, f"{status} {body}")
check("revocation is audited as LOGOUT",
      sql(f"select count(*) from audit.auth_audit where action='LOGOUT' and username='{ADMIN}'") == "1")

print("Client rules")
out, page, code = sign_in(Browser(), authorize_url("admin-dashboard", DASHBOARD_REDIRECT, pkce()[1]), NURSE, PASSWORD)
check("a nurse is refused by the dashboard client", out is None and code == "CLIENT_NOT_ALLOWED", f"{out} {code}")
out, page, code = sign_in(Browser(), authorize_url("admin-dashboard", DASHBOARD_REDIRECT, pkce()[1]), ADMIN, "wrong")
check("a wrong password stays on the login page", out is None and code == "INVALID_CREDENTIALS", f"{out} {code}")
status, _, page, out = Browser().follow(authorize_url("admin-dashboard", "http://evil.example/cb", pkce()[1]))
check("an unregistered redirect_uri is refused without redirecting", out is None and status == 400, f"{status} {out}")
status, _, page, out = Browser().follow(authorize_url("admin-dashboard", DASHBOARD_REDIRECT, "x" * 43, method="plain"))
check("plain PKCE is refused", out is None or "error=" in out, f"{status} {out}")
status, _, page, out = Browser().follow(authorize_url("mobile", MOBILE_REDIRECT, pkce()[1]))
check("mobile without device_id is refused", out is None or "error=" in out, f"{status} {out}")
status, body = token({"grant_type": "password", "client_id": "admin-dashboard", "username": ADMIN, "password": PASSWORD})
check("the dashboard client has no password grant", status == 400 and body.get("error") == "unauthorized_client",
      f"{status} {body}")
status, body = token({"grant_type": "client_credentials", "client_id": "mobile"})
check("unsupported grants are refused", status == 400, f"{status} {body}")
status, body = token({"grant_type": "password", "client_id": "nope", "username": ADMIN, "password": PASSWORD})
check("an unknown client is refused", status in (400, 401) and body.get("error") == "invalid_client", f"{status} {body}")

print("Mobile: code + PKCE with device_id, then the Sync Gateway")
browser = Browser()
verifier, challenge = pkce()
out, page, code = sign_in(browser, authorize_url("mobile", MOBILE_REDIRECT, challenge, DEVICE), NURSE, PASSWORD)
check("the app's custom scheme receives the code", out and out.startswith(MOBILE_REDIRECT + "?"), f"{out} {code}")
mobile_code = code_from(out)
status, mobile = token({"grant_type": "authorization_code", "client_id": "mobile", "code": mobile_code,
                        "redirect_uri": MOBILE_REDIRECT, "code_verifier": "wrong-" + verifier})
check("a wrong PKCE verifier is refused", status == 400, f"{status} {mobile}")

browser = Browser()
verifier, challenge = pkce()
out, _, _ = sign_in(browser, authorize_url("mobile", MOBILE_REDIRECT, challenge, DEVICE), NURSE, PASSWORD)
status, mobile = token({"grant_type": "authorization_code", "client_id": "mobile", "code": code_from(out),
                        "redirect_uri": MOBILE_REDIRECT, "code_verifier": verifier})
mc = claims(mobile["access_token"]) if status == 200 else {}
check("mobile token: device audience, device_id, nurse role",
      mc.get("aud") == "melanin-wound-cdss-devices" and mc.get("device_id") == DEVICE and mc.get("role") == "nurse"
      and mc.get("client_id") == "mobile", mc)
status, _ = api("GET", "/v1/sync/changes?cursor=0&limit=1", token=mobile.get("access_token"))
check("the Sync Gateway accepts the OIDC token", status == 200, status)
check("the device was registered on first sign-in",
      sql(f"select count(*) from clinical.device where device_id='{DEVICE}'") == "1")

print("Revoked device and deactivated clinician")
sql(f"update clinical.device set revoked_at = now() where device_id = '{DEVICE}'")
status, body = token({"grant_type": "refresh_token", "client_id": "mobile", "refresh_token": mobile["refresh_token"]})
check("refresh is refused once the device is revoked", status == 400 and body.get("error") == "invalid_grant",
      f"{status} {body}")
out, page, code = sign_in(Browser(), authorize_url("mobile", MOBILE_REDIRECT, pkce()[1], DEVICE), NURSE, PASSWORD)
check("sign-in from the revoked device is refused", out is None and code == "DEVICE_NOT_ALLOWED", f"{out} {code}")

print("Password grant (simulator and tests only)")
device2 = DEVICE + "-2"
status, pw = token({"grant_type": "password", "client_id": "mobile", "username": NURSE, "password": PASSWORD,
                    "device_id": device2, "scope": "offline_access"})
check("password grant works for the mobile client", status == 200 and pw.get("refresh_token"), f"{status} {pw}")
status, body = token({"grant_type": "password", "client_id": "mobile", "username": NURSE, "password": PASSWORD})
check("password grant without device_id is refused", status == 400, f"{status} {body}")
status, body = token({"grant_type": "password", "client_id": "mobile", "username": NURSE, "password": "wrong",
                      "device_id": device2})
check("a wrong password names the reason", status == 400 and body.get("error_description") == "INVALID_CREDENTIALS",
      f"{status} {body}")
sql(f"update clinical.clinician set active = false where username = '{NURSE}'")
status, body = token({"grant_type": "refresh_token", "client_id": "mobile", "refresh_token": pw["refresh_token"]})
check("refresh is refused once the clinician is deactivated", status == 400, f"{status} {body}")

print("MFA on the login page")
status, admin_tokens = token({"grant_type": "password", "client_id": "mobile", "username": ADMIN,
                              "password": PASSWORD, "device_id": DEVICE + "-mfa", "scope": "offline_access"})
status, enrol = api("POST", "/v1/auth/mfa/enroll", {}, admin_tokens["access_token"])
secret = enrol["secret"]
status, _ = api("POST", "/v1/auth/mfa/confirm", {"code": totp_code(secret)}, admin_tokens["access_token"])
check("MFA enrolment still works with an OIDC token", status == 204, status)
time.sleep(31 - time.time() % 30)  # next TOTP step: the confirm code cannot be replayed
browser = Browser()
verifier, challenge = pkce()
out, page, code = sign_in(browser, authorize_url("admin-dashboard", DASHBOARD_REDIRECT, challenge), ADMIN, PASSWORD,
                          totp="000000")
check("a wrong TOTP code is refused on the TOTP step", out is None and code == "INVALID_TOTP", f"{out} {code}")
fields = form_fields(page)
fields["Totp"] = totp_code(secret)
_, _, page, out = browser.follow(browser.url, fields)
check("the right code completes sign-in without re-entering the password",
      out and out.startswith(DASHBOARD_REDIRECT + "?code="), f"{out} {error_code(page)}")
check("the password is never echoed back into the page", PASSWORD not in page)

print("Login page safety")
status, _, page, _ = Browser().follow("/account/login?returnUrl=https://evil.example/")
check("an external returnUrl is refused", status == 400, status)
status, _, page, _ = Browser().follow("/account/login?returnUrl=/v1/admin/clinicians")
check("a returnUrl other than /connect/authorize is refused", status == 400, status)

sys.exit(t.finish())
