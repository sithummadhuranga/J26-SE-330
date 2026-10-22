"""FR10: the module runs on the device with no network. A static check: nothing in src/colorimetric imports a network library."""

import ast
from pathlib import Path

SRC = Path(__file__).resolve().parents[1] / "src" / "colorimetric"
NETWORK = {"socket", "ssl", "http", "urllib", "urllib3", "requests", "httpx", "aiohttp", "ftplib", "smtplib", "telnetlib", "websocket", "websockets", "grpc"}


def imported_modules(path):
    mods = set()
    for node in ast.walk(ast.parse(path.read_text())):
        if isinstance(node, ast.Import):
            mods.update(a.name.split(".")[0] for a in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module and node.level == 0:
            mods.add(node.module.split(".")[0])
    return mods


def test_no_module_imports_a_network_library():
    offenders = {p.name: sorted(imported_modules(p) & NETWORK) for p in SRC.glob("*.py")}
    assert {k: v for k, v in offenders.items() if v} == {}


def test_the_check_would_notice_one(tmp_path):
    f = tmp_path / "bad.py"
    f.write_text("import os\nfrom urllib.request import urlopen\nimport requests as r\n")
    assert imported_modules(f) & NETWORK == {"urllib", "requests"}
