"""Minimal client for the Toxiproxy API (infra/toxiproxy, port 8474). Standard library only."""
import json
import urllib.request

API = "http://localhost:8474"
PROXY = "sync-gateway"   # listens on 18080, forwards to the API gateway on 8080


def _call(method, path, body=None):
    req = urllib.request.Request(API + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=10) as r:
        raw = r.read()
        return json.loads(raw) if raw else None


def reset():
    """Removes every toxic and re-enables every proxy."""
    _call("POST", "/reset")


def add(name, toxic_type, attributes, stream="downstream", toxicity=1.0):
    """toxicity is the share of connections the toxic applies to (0-1)."""
    _call("POST", f"/proxies/{PROXY}/toxics",
          {"name": name, "type": toxic_type, "stream": stream, "toxicity": toxicity, "attributes": attributes})


def set_enabled(enabled):
    """Disabled = every connection refused and open ones dropped: the hospital Wi-Fi is gone."""
    _call("PATCH", f"/proxies/{PROXY}", {"enabled": enabled})  # updating with POST is deprecated
