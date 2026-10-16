"""Shared helpers for the integration scripts in this folder. Standard library only."""
import base64
import hashlib
import hmac
import html
import json
import os
import re
import secrets
import struct
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar

# Everything goes through the API gateway (ADR 0004).
GATEWAY = os.environ.get("GATEWAY", "http://localhost:8080")
REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


class Checks:
    def __init__(self):
        self.passed = 0
        self.failed = 0

    def check(self, name, condition, detail=""):
        if condition:
            self.passed += 1
            print(f"  PASS  {name}")
        else:
            self.failed += 1
            print(f"  FAIL  {name}  {detail}")

    def finish(self):
        print(f"\n{self.passed} passed, {self.failed} failed")
        return 1 if self.failed else 0


def uuid7():
    ms = int(time.time() * 1000)
    rand = int.from_bytes(secrets.token_bytes(10), "big")
    value = (ms << 80) | (0x7 << 76) | ((rand >> 68) & 0xFFF) << 64 | (0b10 << 62) | (rand & ((1 << 62) - 1))
    h = f"{value:032x}"
    return f"{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}"


def http(method, path, body=None, token=None):
    req = urllib.request.Request(GATEWAY + path, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    data = json.dumps(body).encode() if body is not None else None
    try:
        with urllib.request.urlopen(req, data, timeout=30) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        return e.code, (json.loads(raw) if raw else None)


def pull_all(token, cursor=0, limit=500):
    """Pulls like a device (§7.2): page after page until hasMore is false. Returns (status, changes, next_cursor)."""
    changes = []
    for _ in range(1000):
        status, body = http("GET", f"/v1/sync/changes?cursor={cursor}&limit={limit}", token=token)
        if status != 200:
            return status, changes, cursor
        changes.extend(body["changes"])
        cursor = body["nextCursor"]
        if not body["hasMore"]:
            return status, changes, cursor
    raise AssertionError("pull never reached hasMore=false: the cursor is not advancing")


def sql(query):
    out = subprocess.run(["docker", "exec", "postgres", "psql", "-U", "cdss", "-d", "cdss", "-tAc", query],
                         capture_output=True, text=True, check=True)
    return out.stdout.strip()


def wait_for(query, expected, seconds=20):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if sql(query) == expected:
            return True
        time.sleep(0.5)
    return False


def dotenv():
    """KEY=value pairs from the repo's .env, where docker compose reads the database passwords."""
    values = {}
    try:
        with open(os.path.join(REPO_ROOT, ".env"), encoding="utf-8") as f:
            for line in f:
                key, sep, value = line.strip().partition("=")
                if sep and key and not key.startswith("#"):
                    values[key] = value.split(" #")[0].strip()
    except FileNotFoundError:
        pass
    return values


def db_password(role):
    """A service role's password (identity_svc -> DB_PASSWORD_IDENTITY), as db-migrate set it from .env."""
    name = role.removesuffix("_svc")
    key = f"DB_PASSWORD_{name.upper()}"
    return os.environ.get(key) or dotenv().get(key) or f"{name}-local-dev"


def _identity_env():
    """A local `dotnet run` reads appsettings.json (cdss/cdss); give it the owner login from .env instead."""
    env = dict(os.environ)
    if "ConnectionStrings__Postgres" not in env:
        values = dotenv()
        if "ConnectionStrings__Postgres" in values:
            env["ConnectionStrings__Postgres"] = values["ConnectionStrings__Postgres"]
        elif "POSTGRES_PASSWORD" in values:
            env["ConnectionStrings__Postgres"] = (
                f"Host=localhost;Port=5432;Database={values.get('POSTGRES_DB', 'cdss')};"
                f"Username={values.get('POSTGRES_USER', 'cdss')};Password={values['POSTGRES_PASSWORD']}")
    return env


def create_clinician(username, password, role, facility, full_name="Test User"):
    """Uses the identity service's create-clinician command (the bootstrap path for a facility's first admin)."""
    out = subprocess.run(
        ["dotnet", "run", "--no-build", "--project", "backend/apps/identity-service", "--",
         "create-clinician", username, password, role, facility, full_name],
        cwd=REPO_ROOT, capture_output=True, text=True, env=_identity_env())
    if out.returncode != 0:
        raise RuntimeError(f"create-clinician failed: {out.stderr or out.stdout}")


def token_request(path, form):
    """POSTs OAuth form fields (ADR 0006) and returns (status, parsed JSON or None)."""
    req = urllib.request.Request(GATEWAY + path, data=urllib.parse.urlencode(form).encode(), method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            raw = r.read()
            return r.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read()
        return e.code, (json.loads(raw) if raw else None)


def _tokens(status, body):
    """The token response in the shape these scripts use: accessToken/refreshToken on success, code on failure."""
    if status == 200:
        return status, {"accessToken": body["access_token"], "refreshToken": body.get("refresh_token"),
                        "expiresIn": body["expires_in"], "idToken": body.get("id_token")}
    body = body or {}
    return status, {"code": body.get("error_description") or body.get("error"), "error": body.get("error")}


def login(username, password, device, totp=None):
    """A phone signing in: the password grant the local stack enables for tests (Oidc:AllowPasswordGrant).
    Failures are 400 with code = the reason (INVALID_CREDENTIALS, CREDENTIAL_LOCKED, MFA_REQUIRED, ...)."""
    form = {"grant_type": "password", "client_id": "mobile", "username": username, "password": password,
            "device_id": device, "scope": "openid offline_access"}
    if totp:
        form["totp"] = totp
    return _tokens(*token_request("/connect/token", form))


def refresh(refresh_token, client_id="mobile"):
    """Rotates both tokens. A spent, revoked or expired refresh token is 400 invalid_grant."""
    return _tokens(*token_request("/connect/token", {"grant_type": "refresh_token", "client_id": client_id,
                                                     "refresh_token": refresh_token}))


def logout(refresh_token, client_id="mobile"):
    """Ends the session (token revocation); always 200."""
    status, _ = token_request("/connect/revocation", {"client_id": client_id, "token": refresh_token,
                                                      "token_type_hint": "refresh_token"})
    return status


DASHBOARD_REDIRECT = "http://localhost:3001/callback"
MOBILE_REDIRECT = "lk.sliit.cdss:/oauth2redirect"


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


class Browser:
    """A user agent for the login page: keeps cookies, follows redirects inside the gateway, and stops when the
    identity service sends it to a client's redirect URI."""

    def __init__(self):
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(CookieJar()),
                                                  _NoRedirect)
        self.url = None  # the last page loaded; the login form posts back to it

    def request(self, url, form=None):
        data = urllib.parse.urlencode(form).encode() if form is not None else None
        self.url = url if url.startswith(("http://", "https://")) else GATEWAY + url
        try:
            with self.opener.open(urllib.request.Request(self.url, data=data), timeout=30) as r:
                return r.status, r.headers, r.read().decode()
        except urllib.error.HTTPError as e:
            return e.code, e.headers, e.read().decode()

    def follow(self, url, form=None):
        """Returns (final status, headers, body, the URL the browser left the gateway for, or None)."""
        status, headers, body = self.request(url, form)
        for _ in range(10):
            location = headers.get("Location")
            if status not in (301, 302, 303) or not location:
                return status, headers, body, None
            if not (location.startswith("/") or location.startswith(GATEWAY)):
                return status, headers, body, location
            status, headers, body = self.request(location)
        raise AssertionError("too many redirects")


def form_fields(page):
    return {html.unescape(n): html.unescape(v) for n, v in
            re.findall(r'<input[^>]*?name="([^"]+)"[^>]*?value="([^"]*)"', page)}


def page_error(page):
    """The reason the login page shows (data-code), e.g. CLIENT_NOT_ALLOWED."""
    m = re.search(r'data-code="([^"]+)"', page)
    return m.group(1) if m else None


def pkce():
    verifier = secrets.token_urlsafe(48)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    return verifier, challenge


def authorize_url(client_id, redirect_uri, challenge, device=None, method="S256", scope="openid offline_access"):
    params = {"client_id": client_id, "redirect_uri": redirect_uri, "response_type": "code", "scope": scope,
              "state": secrets.token_hex(8), "code_challenge": challenge, "code_challenge_method": method}
    if device:
        params["device_id"] = device
    return "/connect/authorize?" + urllib.parse.urlencode(params)


def sign_in(browser, url, username, password, totp=None):
    """Runs the login page; returns (where the browser was sent, last page, reason shown on the page)."""
    _, _, page, out = browser.follow(url)
    if out:
        return out, page, None
    fields = form_fields(page)
    fields.update(Username=username, Password=password)
    _, _, page, out = browser.follow(browser.url, fields)
    if out or page_error(page) or totp is None:
        return out, page, page_error(page)
    fields = form_fields(page)
    fields["Totp"] = totp
    _, _, page, out = browser.follow(browser.url, fields)
    return out, page, page_error(page)


def code_from(location):
    query = urllib.parse.parse_qs(urllib.parse.urlparse(location or "").query)
    return query.get("code", [None])[0]


def dashboard_login(username, password, totp=None):
    """The admin dashboard's sign-in: code + PKCE through the login page. Failures are 400 with code = the reason the
    page showed (CLIENT_NOT_ALLOWED, INVALID_CREDENTIALS, ...)."""
    verifier, challenge = pkce()
    out, _, reason = sign_in(Browser(), authorize_url("admin-dashboard", DASHBOARD_REDIRECT, challenge,
                                                      scope="openid profile offline_access"), username, password, totp)
    if not code_from(out):
        return 400, {"code": reason}
    return _tokens(*token_request("/connect/token", {
        "grant_type": "authorization_code", "client_id": "admin-dashboard", "code": code_from(out),
        "redirect_uri": DASHBOARD_REDIRECT, "code_verifier": verifier}))


def totp_code(base32_secret, step=None):
    """RFC 6238 code, matching the gateway's Totp class (SHA-1, 30 s, 6 digits)."""
    key = base64.b32decode(base32_secret + "=" * (-len(base32_secret) % 8))
    step = int(time.time()) // 30 if step is None else step
    digest = hmac.new(key, struct.pack(">Q", step), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return f"{value % 1_000_000:06d}"


def wound_event(assessment_id, wound_id, device, facility, revision=1, **overrides):
    evt = {
        "schemaVersion": "1.0",
        "eventId": uuid7(),
        "assessmentId": assessment_id,
        "revision": revision,
        "woundId": wound_id,
        "patientRef": "p-" + secrets.token_hex(4),
        "deviceId": device,
        "facilityId": facility,
        "capturedAt": "2026-10-03T09:41:12+05:30",
        "analytics": {
            "areaMm2": 412.6,
            "colourRegions": [{"cluster": 1, "percent": 61.2}, {"cluster": 2, "percent": 27.9}],
            "fitzpatrickClass": "V",
            "pipeline": {"calibration": "2.1.0", "segmentation": "yolo11n-seg-0.4"},
        },
        "clinicalAssessment": {"pedalPulses": "not_recorded", "protectiveSensation": "absent"},
    }
    evt.update(overrides)
    return evt
