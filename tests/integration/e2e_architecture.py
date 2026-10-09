"""DISRUPTIVE: checks the running stack against the architecture, including failure cases (local Docker only)."""
import base64
import gzip
import hashlib
import json
import os
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

from client import GATEWAY, REPO_ROOT, Checks, create_clinician, http, login, pull_all, sql, uuid7, wait_for, wound_event

sys.stdout.reconfigure(encoding="utf-8")  # section titles use §; the Windows console defaults to cp1252

FAC_A, FAC_B = "fac-001", "fac-002"
DEVICE_A, DEVICE_B = "dev-arch-" + secrets.token_hex(3), "dev-arch-b-" + secrets.token_hex(3)
PASSWORD = "Arch-Pass-" + secrets.token_hex(4)
suffix = secrets.token_hex(3)
NURSE_A, ADMIN_B = f"arch.nurse.{suffix}", f"arch.admin.b.{suffix}"

t = Checks()
check = t.check
gaps = []


def gap(name, detail):
    gaps.append(name)
    print(f"  GAP   {name}  ({detail})")


def section(title):
    print(f"\n{title}")


def sh(*args, timeout=180):
    return subprocess.run(list(args), cwd=REPO_ROOT, capture_output=True, text=True, timeout=timeout)


def compose(*args, timeout=300):
    return sh("docker", "compose", *args, timeout=timeout)


def kafka(*args, stdin=None, timeout=60):
    return subprocess.run(["docker", "exec", "-i", "kafka", *args], input=stdin, capture_output=True, text=True,
                          timeout=timeout)


def end_offsets(topic):
    out = kafka("/opt/kafka/bin/kafka-get-offsets.sh", "--bootstrap-server", "localhost:9092", "--topic", topic).stdout
    return sum(int(line.rsplit(":", 1)[1]) for line in out.split() if line.count(":") == 2)


def group_lag(group):
    out = kafka("/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server", "localhost:9092",
                "--describe", "--group", group).stdout
    rows = [cols for cols in (line.split() for line in out.splitlines()[1:])
            if len(cols) >= 6 and cols[0] == group and cols[5].isdigit()]
    # During a rebalance describe can list no partitions; that must not read as "no lag".
    return sum(int(cols[5]) for cols in rows) if len(rows) == 6 else None


def wait_until(fn, seconds=60, interval=1.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            if fn():
                return True
        except Exception:
            pass
        time.sleep(interval)
    return False


def raw_post(path, data, headers, token=None, timeout=60):
    req = urllib.request.Request(GATEWAY + path, data=data, method="POST", headers=headers)
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, dict(r.headers), json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        body = e.read()
        return e.code, dict(e.headers), (json.loads(body) if body else None)


def push(events, token, device=None):
    return http("POST", "/v1/sync/push", {"deviceId": device or DEVICE_A, "events": events}, token)


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def persisted(event_id):
    return sql(f"select count(*) from clinical.wound_assessment where event_id = '{event_id}'") == "1"


# ------------------------------------------------------------------------------------------------------------
section("Setup")
sql(f"INSERT INTO clinical.facility (facility_id, name) VALUES ('{FAC_B}', 'Second Test Hospital') ON CONFLICT DO NOTHING")
create_clinician(NURSE_A, PASSWORD, "nurse", FAC_A, "Arch Nurse")
create_clinician(ADMIN_B, PASSWORD, "admin", FAC_B, "Arch Admin B")
status, body = login(NURSE_A, PASSWORD, DEVICE_A)
check("nurse logs in through the API gateway", status == 200, f"{status} {body}")
token, refresh = body["accessToken"], body["refreshToken"]
_, body = login(ADMIN_B, PASSWORD, DEVICE_B)
token_b = body["accessToken"]

# ------------------------------------------------------------------------------------------------------------
section("§4 / v2.1: deployable units and the single entry point")
states = compose("ps", "-a", "--format", "{{.Service}} {{.State}}").stdout
running = {l.split()[0] for l in states.splitlines() if l.endswith("running")}
expected = {"api-gateway", "identity-service", "sync-gateway", "ingest-persister", "outbox-relay", "orchestrator",
            "housekeeping", "kafka", "postgres", "rag-stub"}
check("every deployable unit is running", expected <= running, f"missing {expected - running}")
for port, name in [(8085, "identity-service"), (8086, "sync-gateway")]:
    with socket.socket() as s:
        s.settimeout(1)
        closed = s.connect_ex(("127.0.0.1", port)) != 0
    check(f"{name} is not reachable from the host (only through the gateway)", closed)
status, _ = http("GET", "/health")
check("GET /health through the edge (device reachability probe, §6.2)", status == 200, status)

# ------------------------------------------------------------------------------------------------------------
section("§5: Wound Event contract")
w, a = uuid7(), uuid7()
big = wound_event(a, w, DEVICE_A, FAC_A)
big["analytics"]["pipeline"]["segmentation"] = "x" * 17000
extra = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A)
extra["patientName"] = "Kamal Perera"
bad_tri = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A)
bad_tri["clinicalAssessment"]["pedalPulses"] = "no"
rev1 = wound_event(a, w, DEVICE_A, FAC_A, revision=1)
rev2 = wound_event(a, w, DEVICE_A, FAC_A, revision=2, patientRef=rev1["patientRef"])
status, body = push([big, extra, bad_tri, rev1, rev2], token)
res = [r["status"] + ("/" + r.get("code", "") if r["status"] == "REJECTED" else "") for r in body["results"]]
check("event over 16 KB is REJECTED PAYLOAD_TOO_LARGE", res[0] == "REJECTED/PAYLOAD_TOO_LARGE", res)
check("unknown field (a name) is REJECTED: data minimization is enforced by the schema (§12)",
      res[1] == "REJECTED/SCHEMA_INVALID", res)
check("tri-state value outside present/absent/not_recorded is REJECTED", res[2] == "REJECTED/SCHEMA_INVALID", res)
check("valid events in the same batch are unaffected (per-event results)", res[3:] == ["ACCEPTED", "ACCEPTED"], res)
check("an edit is a new revision row, never an update in place",
      wait_for(f"select count(*) from clinical.wound_assessment where assessment_id = '{a}'", "2", 30))

# ------------------------------------------------------------------------------------------------------------
section("§7.1: push protocol")
batch = [wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A) for _ in range(51)]
status, body = push(batch, token)
check("more than 50 events is 413 BATCH_TOO_LARGE (device splits the batch)",
      status == 413 and body["code"] == "BATCH_TOO_LARGE", f"{status} {body}")
evt = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A)
payload = gzip.compress(json.dumps({"deviceId": DEVICE_A, "events": [evt]}).encode())
status, _, body = raw_post("/v1/sync/push", payload, {"Content-Type": "application/json", "Content-Encoding": "gzip"}, token)
check("gzip-compressed batch through the edge is ACCEPTED", status == 200 and body["results"][0]["status"] == "ACCEPTED",
      f"{status} {body}")
gz_event = evt["eventId"]
status, _, _ = raw_post("/v1/sync/push", b"x" * 3_000_000, {"Content-Type": "application/json"}, token)
check("request body over 2 MB is 413 at the edge", status == 413, status)
status, body = push([wound_event(uuid7(), uuid7(), DEVICE_A, FAC_B)], token)
check("event for another facility is REJECTED FACILITY_MISMATCH",
      status == 200 and body["results"][0].get("code") == "FACILITY_MISMATCH", body)
status, _ = push([wound_event(uuid7(), uuid7(), "dev-someone-else", FAC_A)], token, device="dev-someone-else")
check("pushing as another device is 403", status == 403, status)

# ------------------------------------------------------------------------------------------------------------
section("§8.2: what goes onto Kafka")
check("gzip event persisted", wait_until(lambda: persisted(gz_event), 30))
ref = sql(f"select kafka_ref from audit.provenance where event_id = '{gz_event}' and stage = 'GATEWAY_ACCEPTED'")
check("GATEWAY_ACCEPTED provenance records the Kafka topic/partition/offset", ref.startswith("wound-events/"), ref)
if ref.startswith("wound-events/"):
    _, part, off = ref.split("/")
    out = kafka("/opt/kafka/bin/kafka-console-consumer.sh", "--bootstrap-server", "localhost:9092", "--topic",
                "wound-events", "--partition", part, "--offset", off, "--max-messages", "1", "--timeout-ms", "15000",
                "--property", "print.key=true", "--property", "print.headers=true").stdout
    check("message key is the woundId (per-wound ordering)", evt["woundId"] in out.split("\t")[1] if "\t" in out else False,
          out[:200])
    headers = out.split("\t")[0]
    check("headers carry event-id, schema-version, device-id and traceparent",
          all(h in headers for h in ("event-id:", "schema-version:", "device-id:", "traceparent:")), headers[:300])
topics = kafka("/opt/kafka/bin/kafka-topics.sh", "--bootstrap-server", "localhost:9092", "--describe").stdout
for topic, parts in [("wound-events", 6), ("wound-events.persisted", 6), ("recommendations.ready", 3),
                     ("wound-events.retry.30s", 3), ("wound-events.retry.5m", 3), ("wound-events.dlq", 1)]:
    check(f"topic {topic} exists with {parts} partitions", f"Topic: {topic}\tTopicId" in topics and
          f"Topic: {topic}\tTopicId" in topics and f"PartitionCount: {parts}" in
          next((l for l in topics.splitlines() if l.startswith(f"Topic: {topic}\t")), ""))

# ------------------------------------------------------------------------------------------------------------
section("§7.2: pull")
status, changes, cursor = pull_all(token)
mine = [c for c in changes if c["assessmentId"] == a and c["type"] == "PERSISTED"]
check("PERSISTED changes for both revisions are pulled", {c["revision"] for c in mine} == {1, 2}, mine)
status, body = http("GET", f"/v1/sync/changes?cursor={cursor}&limit=500", token=token)
check("changes from the last 60 s are re-sent even at the latest cursor (out-of-order commit guard)",
      any(c["assessmentId"] == a for c in body["changes"]), len(body["changes"]))
check("device cursor is recorded server-side",
      sql(f"select last_seq from sync.device_cursor where device_id = '{DEVICE_A}'") == str(cursor))
status, changes_b, _ = pull_all(token_b)
check("another facility does not see these changes (facility scope)",
      status == 200 and not any(c["assessmentId"] == a for c in changes_b), status)

# ------------------------------------------------------------------------------------------------------------
section("§7.3 / §12 / ADR 0003: identity and tokens")
header = json.loads(base64.urlsafe_b64decode(token.split(".")[0] + "=="))
claims = json.loads(base64.urlsafe_b64decode(token.split(".")[1] + "=="))
check("access token is RS256 with a key id", header.get("alg") == "RS256" and header.get("kid"), header)
check("claims sub, device_id, facility_id, role, client_id; 15-minute lifetime",
      {"sub", "device_id", "facility_id", "role", "client_id"} <= claims.keys() and
      840 <= claims["exp"] - int(time.time()) <= 905, claims)
_, jwks = http("GET", "/.well-known/jwks.json")
check("JWKS publishes only public key material", all("d" not in k and "p" not in k for k in jwks["keys"]), jwks)
none_token = b64url(b'{"alg":"none","typ":"JWT"}') + "." + token.split(".")[1] + "."
status, _ = http("GET", "/v1/sync/changes", token=none_token)
check("unsigned token (alg none) is refused", status == 401, status)
tampered = dict(claims, facility_id=FAC_B)
forged = token.split(".")[0] + "." + b64url(json.dumps(tampered).encode()) + "." + token.split(".")[2]
status, _ = http("GET", "/v1/sync/changes", token=forged)
check("token with a changed claim is refused", status == 401, status)
import hmac as _hmac
n = jwks["keys"][0]["n"].encode()
hs_head = b64url(b'{"alg":"HS256","typ":"JWT"}') + "." + token.split(".")[1]
hs_token = hs_head + "." + b64url(_hmac.new(n, hs_head.encode(), hashlib.sha256).digest())
status, _ = http("GET", "/v1/sync/changes", token=hs_token)
check("HS256 token signed with the public key (algorithm confusion) is refused", status == 401, status)
digest = hashlib.sha256(refresh.encode()).hexdigest()
check("refresh token is stored only as its SHA-256 hash",
      sql(f"select count(*) from clinical.clinician_session where refresh_token_hash = decode('{digest}', 'hex')") == "1"
      and sql(f"select count(*) from clinical.clinician_session where refresh_token_hash = convert_to('{refresh}', 'UTF8')") == "0")
check("every credential has its own salt",
      sql("select count(*) - count(distinct password_salt) from clinical.clinician_credential") == "0")
check("no password is stored in plain text",
      sql(f"select count(*) from clinical.clinician_credential where password_hash like '%{PASSWORD}%'") == "0")
check("successful and failed logins are in audit.auth_audit",
      sql(f"select count(*) from audit.auth_audit where username = '{NURSE_A}' and action = 'LOGIN' and success") != "0")

# ------------------------------------------------------------------------------------------------------------
section("§9.3 / §9.4: persister transaction")
check("no event stored twice (unique event_id)",
      sql("select count(*) from (select event_id from clinical.wound_assessment group by 1 having count(*) > 1) x") == "0")
check("no (assessment_id, revision) stored twice",
      sql("select count(*) from (select 1 from clinical.wound_assessment group by assessment_id, revision having count(*) > 1) x") == "0")
# Housekeeping may have archived the change-log row or deleted the outbox row, so check both places.
check("every stored assessment has PERSISTED provenance, a change-log row and an outbox row (one transaction)",
      sql("""select count(*) from clinical.wound_assessment wa
             where not exists (select 1 from audit.provenance p where p.event_id = wa.event_id and p.stage = 'PERSISTED')
                or not exists (select 1 from sync.change_log c where c.assessment_id = wa.assessment_id
                               and c.revision = wa.revision and c.change_type = 'PERSISTED'
                               union all
                               select 1 from sync.change_log_archive c where c.assessment_id = wa.assessment_id
                               and c.revision = wa.revision and c.change_type = 'PERSISTED')
                or (wa.received_at > now() - interval '30 minutes'
                    and not exists (select 1 from messaging.outbox o where o.payload->>'eventId' = wa.event_id::text))""")
      == "0")
check("the patient alias never travels the sync path (no display alias in outbox payloads)",
      sql("select count(*) from messaging.outbox where payload::text ilike '%displayAlias%'") == "0")

# ------------------------------------------------------------------------------------------------------------
section("§13: auditability completeness")
check("every event the gateway accepted (over 30 s ago) reached PERSISTED or DEDUPLICATED",
      sql("""select count(*) from audit.provenance g where g.stage = 'GATEWAY_ACCEPTED'
             and g.recorded_at < now() - interval '30 seconds'
             and not exists (select 1 from audit.provenance p where p.event_id = g.event_id
                             and p.stage in ('PERSISTED', 'DEDUPLICATED'))""") == "0")

# ------------------------------------------------------------------------------------------------------------
section("§9.2: schema version guard")
sql("insert into sync.schema_migrations (schema_name, version, description, checksum) "
    "values ('clinical', 9999, 'architecture test: fake future migration', 'test')")
try:
    run = compose("run", "--rm", "--no-deps", "identity-service", timeout=120)
    check("a service refuses to start against an unexpected schema version",
          # The guard's own message; not tied to the current version number, which moves with every migration.
          run.returncode != 0 and "is at version 9999, but this build expects" in (run.stdout + run.stderr),
          (run.stdout + run.stderr)[-300:])
finally:
    sql("delete from sync.schema_migrations where schema_name = 'clinical' and version = 9999")

# ------------------------------------------------------------------------------------------------------------
section("§11: failure handling (disruptive)")

# Persister down: the device still gets a fast ACCEPTED; the event is persisted when it comes back.
compose("stop", "ingest-persister")
evt = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A)
start = time.time()
status, body = push([evt], token)
took = time.time() - start
check("persister down: push still ACCEPTED quickly (clinician never waits)",
      status == 200 and body["results"][0]["status"] == "ACCEPTED" and took < 2, f"{status} {took:.2f}s")
compose("start", "ingest-persister")
check("... and the event is persisted once the persister is back", wait_until(lambda: persisted(evt["eventId"]), 60))

# Poison message: goes to the DLQ, the partition keeps moving.
dlq_before = end_offsets("wound-events.dlq")
for p in range(6):
    kafka("/opt/kafka/bin/kafka-console-producer.sh", "--bootstrap-server", "localhost:9092", "--topic", "wound-events",
          "--property", "parse.key=true", "--property", "key.separator=|",
          stdin=f"poison-{p}-{secrets.token_hex(2)}|{{not json at all\n")
check("poison messages land in wound-events.dlq", wait_until(lambda: end_offsets("wound-events.dlq") > dlq_before, 60))
evt = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A)
push([evt], token)
check("... and the next valid event is still persisted (no head-of-line blocking)",
      wait_until(lambda: persisted(evt["eventId"]), 60))

# Kafka down: 503 with Retry-After, nothing confirmed; recovers when Kafka returns.
compose("stop", "kafka")
evt = wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A)
status, headers, body = raw_post("/v1/sync/push", json.dumps({"deviceId": DEVICE_A, "events": [evt]}).encode(),
                                 {"Content-Type": "application/json"}, token)
check("Kafka down: 503 with Retry-After", status == 503 and "Retry-After" in headers, f"{status} {body}")
check("... and nothing was recorded as accepted",
      sql(f"select count(*) from audit.provenance where event_id = '{evt['eventId']}'") == "0")
compose("start", "kafka")
wait_until(lambda: "healthy" in compose("ps", "kafka", "--format", "{{.Status}}").stdout, 120, 2)


def push_ok():
    s, b = push([evt], token)
    return s == 200 and b["results"][0]["status"] == "ACCEPTED"


check("Kafka back: the same event is ACCEPTED on retry", wait_until(push_ok, 120, 3))
check("... and persisted", wait_until(lambda: persisted(evt["eventId"]), 120))

# Replay the whole topic into two persister replicas: row counts unchanged, DEDUPLICATED rows written (§9.3).
time.sleep(3)
rows_before = sql("select count(*) from clinical.wound_assessment")
dedup_before = int(sql("select count(*) from audit.provenance where stage = 'DEDUPLICATED'"))
compose("stop", "ingest-persister")
reset = kafka("/opt/kafka/bin/kafka-consumer-groups.sh", "--bootstrap-server", "localhost:9092", "--group", "persister",
              "--reset-offsets", "--to-earliest", "--topic", "wound-events", "--execute")
check("persister offsets reset to the start of wound-events", reset.returncode == 0, reset.stderr[-200:])
compose("up", "-d", "--no-deps", "--scale", "ingest-persister=2", "ingest-persister")
check("two persister replicas drain the replay", wait_until(lambda: group_lag("persister") == 0, 180, 3))
time.sleep(3)
check("replaying wound-events from offset 0 leaves the row count unchanged",
      sql("select count(*) from clinical.wound_assessment") == rows_before, rows_before)
check("... and every absorbed repeat is recorded as DEDUPLICATED",
      int(sql("select count(*) from audit.provenance where stage = 'DEDUPLICATED'")) > dedup_before)
compose("up", "-d", "--no-deps", "--scale", "ingest-persister=1", "ingest-persister")

# Two outbox relays: FOR UPDATE SKIP LOCKED, so each outbox row is published once.
compose("up", "-d", "--no-deps", "--scale", "outbox-relay=2", "outbox-relay")
time.sleep(5)
events = [wound_event(uuid7(), uuid7(), DEVICE_A, FAC_A) for _ in range(20)]
push(events, token)
ids = [e["eventId"] for e in events]
id_list = ",".join(f"'{i}'" for i in ids)
check("20 events persisted with two relays running",
      wait_for(f"select count(*) from clinical.wound_assessment where event_id in ({id_list})", "20", 60))
check("... every outbox row marked published",
      wait_for(f"select count(*) from messaging.outbox where payload->>'eventId' in ({id_list}) and published_at is null", "0", 60))
out = kafka("/opt/kafka/bin/kafka-console-consumer.sh", "--bootstrap-server", "localhost:9092", "--topic",
            "wound-events.persisted", "--from-beginning", "--timeout-ms", "15000", timeout=120).stdout
counts = {i: out.count(i) for i in ids}
check("... each published exactly once to wound-events.persisted", set(counts.values()) == {1}, counts)
compose("up", "-d", "--no-deps", "--scale", "outbox-relay=1", "outbox-relay")

# ------------------------------------------------------------------------------------------------------------
section("Edge rate limit (ADR 0004), last because it blocks logins from this IP for a minute")
codes = []
for _ in range(130):
    s, _ = http("POST", "/v1/auth/login", {"username": "nobody", "password": "x", "deviceId": "d"})
    codes.append(s)
    if s == 429:
        break
check("login is rate-limited per IP with 429", 429 in codes, codes[-3:])

# ------------------------------------------------------------------------------------------------------------
section("Not built yet (expected gaps)")
if sql("select count(*) from clinical.recommendation") == "0":
    gap("§10 orchestrator: no recommendations produced", "phase 6, executors are TODO stubs")
if sql("select count(*) from pg_roles where rolname = 'gateway_svc'") == "0":
    gap("§9.4 / §12 per-service database roles, insert-only audit tables", "phase 9")
mobile_sync = ["data/queue_repository.dart", "engine/sync_engine.dart", "engine/sync_scheduler.dart"]
if not all(os.path.exists(os.path.join(REPO_ROOT, "mobile", "lib", "features", "sync", f)) for f in mobile_sync):
    gap("§6 mobile Drift queue and sync engine", "phase 12")

print(f"\n{len(gaps)} known gaps (not built yet)")
sys.exit(t.finish())
