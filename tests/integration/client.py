"""Shared helpers for the integration scripts in this folder. Standard library only."""
import base64
import hashlib
import hmac
import json
import os
import secrets
import struct
import subprocess
import time
import urllib.error
import urllib.request

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


def login(username, password, device, totp=None):
    body = {"username": username, "password": password, "deviceId": device}
    if totp:
        body["totp"] = totp
    return http("POST", "/v1/auth/login", body)


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
