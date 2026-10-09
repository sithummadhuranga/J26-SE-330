"""DISRUPTIVE: tests the figures proxy through the gateway, including a 503 from the stub (local Docker only)."""
import os
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

from client import GATEWAY, REPO_ROOT, Checks, http, login

sys.stdout.reconfigure(encoding="utf-8")
PNG = b"\x89PNG\r\n\x1a\n"

t = Checks()
check = t.check


def get(path, token=None, headers=None):
    req = urllib.request.Request(GATEWAY + path, headers=headers or {})
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return r.status, dict(r.headers), r.read()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read()


def stub(mode):
    subprocess.run(["docker", "compose", "up", "-d", "--no-deps", "--force-recreate", "rag-stub"], cwd=REPO_ROOT,
                   capture_output=True, env={**os.environ, "STUB_FAIL_MODE": mode})
    for _ in range(60):
        if subprocess.run(["curl", "-s", "-o", os.devnull, "-w", "%{http_code}", "http://localhost:5080/health"],
                          capture_output=True, text=True).stdout == "200":
            return
        time.sleep(1)


_, body = login("n.silva", "Demo-Pass-2026!", "dev-fig-" + secrets.token_hex(3))
token = body["accessToken"]

print("§10.4: a figure through the gateway")
status, headers, data = get("/v1/figures/stub-0/F1", token)
check("200 with the image bytes", status == 200 and data.startswith(PNG), f"{status} {data[:20]!r}")
check("Content-Type passed through", headers.get("Content-Type") == "image/png", headers.get("Content-Type"))
check("license and attribution passed through",
      headers.get("X-Figure-Licence", "").startswith("CC BY-NC") and "Stub corpus" in headers.get("X-Figure-Attribution", ""),
      headers)
check("tier passed through", headers.get("X-Figure-Tier") == "A", headers.get("X-Figure-Tier"))
check("cacheable for good (a corpus version is frozen)", "immutable" in headers.get("Cache-Control", ""),
      headers.get("Cache-Control"))
etag = headers.get("ETag")
status, _, data = get("/v1/figures/stub-0/F1", token, {"If-None-Match": etag})
check("If-None-Match with the ETag: 304, no body", status == 304 and not data, f"{status} {etag}")

print("\nErrors")
status, _, data = get("/v1/figures/stub-0/F99", token)
check("unknown figure: 404 FIGURE_NOT_FOUND", status == 404 and b"FIGURE_NOT_FOUND" in data, f"{status} {data[:80]!r}")
for bad in ["stub-0/..%2F..%2Fhealth", "stub-0/F1%3Fx%3D1", "stub-0/F1..", "stub-0/" + "a" * 101]:
    status, _, data = get(f"/v1/figures/{bad}", token)
    check(f"unsafe id refused by the proxy, never forwarded ({bad[:30]})",
          status == 400 and b"INVALID_FIGURE_ID" in data, f"{status} {data[:60]!r}")
# An encoded ".." gets collapsed so nothing matches (empty 404); a forwarded one would say FIGURE_NOT_FOUND.
status, _, data = get("/v1/figures/%2E%2E/F1", token)
check("encoded '..' never reaches the proxy (no route)", status == 404 and b"FIGURE_NOT_FOUND" not in data,
      f"{status} {data[:60]!r}")
status, _, _ = get("/v1/figures/stub-0/F1")
check("no token: 401", status == 401, status)
dash_status, dash = http("POST", "/v1/auth/login",
                         {"username": "admin.demo", "password": "Demo-Admin-2026!", "clientId": "admin-dashboard"})
status, _, _ = get("/v1/figures/stub-0/F1", dash["accessToken"])
check("admin-dashboard token: 401 (devices only)", status == 401, status)

print("\nRecommendation Service unavailable")
stub("503")
try:
    status, headers, data = get("/v1/figures/stub-0/F2", token)
    check("502 FIGURE_UNAVAILABLE with Retry-After", status == 502 and b"FIGURE_UNAVAILABLE" in data and "Retry-After" in headers,
          f"{status} {data[:80]!r}")
finally:
    stub("none")
status, _, data = get("/v1/figures/stub-0/F2", token)
check("works again once the service is back", status == 200 and data.startswith(PNG), status)

sys.exit(t.finish())
