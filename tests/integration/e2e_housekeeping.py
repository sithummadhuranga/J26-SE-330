"""Housekeeping must never archive a change some active device hasn't pulled yet; safe to re-run."""
import secrets
import subprocess
import sys

from client import REPO_ROOT, Checks, create_clinician, http, login, pull_all, sql, uuid7

suffix = secrets.token_hex(3)
FAC = f"fac-hk-{suffix}"
PASSWORD = "Housekeeping-" + secrets.token_hex(4)
NURSE, ADMIN = f"nurse.hk.{suffix}", f"admin.hk.{suffix}"
PHONE_A, PHONE_B, LOST = f"dev-hk-a-{suffix}", f"dev-hk-b-{suffix}", f"dev-hk-lost-{suffix}"

t = Checks()
check = t.check


def run_cycle():
    out = subprocess.run(["docker", "compose", "run", "--rm", "--no-deps", "housekeeping", "run-once"],
                         cwd=REPO_ROOT, capture_output=True, text=True)
    lines = [l for l in out.stdout.splitlines() if l.startswith(("ran=", "held_back "))]
    if out.returncode != 0 or not lines:
        raise SystemExit(f"housekeeping run-once failed:\n{out.stdout[-2000:]}\n{out.stderr[-2000:]}")
    summary = dict(kv.split("=", 1) for kv in lines[0].split())
    held = [dict(kv.split("=", 1) for kv in l.split()[1:]) for l in lines[1:]]
    return summary, held


def change_row(age="2 days"):
    return int(sql(f"""insert into sync.change_log (facility_id, change_type, assessment_id, revision, created_at)
                       values ('{FAC}', 'PERSISTED', '{uuid7()}', 1, now() - interval '{age}') returning server_seq"""
                   ).splitlines()[0])


def in_log(seqs):
    return int(sql(f"select count(*) from sync.change_log where server_seq in ({','.join(map(str, seqs))})"))


def in_archive(seqs):
    return int(sql(f"select count(*) from sync.change_log_archive where server_seq in ({','.join(map(str, seqs))})"))


def pull_page(token, cursor, limit):
    status, body = http("GET", f"/v1/sync/changes?cursor={cursor}&limit={limit}", token=token)
    assert status == 200, (status, body)
    return [c["seq"] for c in body["changes"] if c["seq"] > cursor], body["nextCursor"]


print("Setup: a facility with two phones in use and one lost phone that never pulls")
sql(f"INSERT INTO clinical.facility (facility_id, name) VALUES ('{FAC}', 'Housekeeping Test') ON CONFLICT DO NOTHING")
create_clinician(NURSE, PASSWORD, "nurse", FAC, "HK Nurse")
create_clinician(ADMIN, PASSWORD, "admin", FAC, "HK Admin")
phone_a = login(NURSE, PASSWORD, PHONE_A)[1]["accessToken"]
phone_b = login(NURSE, PASSWORD, PHONE_B)[1]["accessToken"]
login(NURSE, PASSWORD, LOST)
# The admin dashboard has no device, so it holds nothing back.
status, body = http("POST", "/v1/auth/login", {"username": ADMIN, "password": PASSWORD, "clientId": "admin-dashboard"})
admin = body["accessToken"]

old = [change_row() for _ in range(6)]
recent = change_row(age="1 minute")
everything = old + [recent]

# Phone A has everything and says so (pull records the cursor the phone sends, i.e. what it already has).
status, got_a, cursor_a = pull_all(phone_a)
pull_page(phone_a, cursor_a, 1)
check("phone A received all of the facility's rows", set(everything) <= {c["seq"] for c in got_a})
# Phone B pulls two pages of two, so it has acknowledged only the first two rows (it sent cursor old[1] last).
seen_b, cursor_b = pull_page(phone_b, 0, 2)
seen_b2, _ = pull_page(phone_b, cursor_b, 2)
check("phone B's pages were the first rows", seen_b == old[:2] and seen_b2 == old[2:4], (seen_b, seen_b2))
check("phone B's server cursor is at the second row",
      sql(f"select last_seq from sync.device_cursor where device_id = '{PHONE_B}'") == str(old[1]))

print("\nCycle 1: the lost phone has never pulled, so nothing of this facility is archived")
summary, held = run_cycle()
check("housekeeping ran", summary.get("ran") == "True", summary)
check("no row of the facility was archived", in_log(everything) == 7 and in_archive(everything) == 0)
lost_held = [h for h in held if h["facility"] == FAC]
check("the lost phone is reported as holding archival back",
      len(lost_held) == 1 and lost_held[0]["device"] == LOST and lost_held[0]["cursor"] == "0", lost_held)

print("\nAdmin revokes the lost phone (A1); cycle 2")
status, _ = http("POST", f"/v1/admin/devices/{LOST}/revoke", token=admin)
check("revoked (204)", status == 204, status)
summary, held = run_cycle()
check("the rows every phone has acknowledged are archived (up to phone B's cursor)",
      in_archive(old[:2]) == 2 and in_log(old[:2]) == 0)
check("rows phone B has not acknowledged stay in the change log", in_log(old[2:]) == 4 and in_archive(old[2:]) == 0)
check("a row younger than the margin stays, though every cursor has passed it", in_log([recent]) == 1)
check("phone B is now the one holding the facility back",
      [h["device"] for h in held if h["facility"] == FAC] == [PHONE_B], held)
check("archived rows keep their columns",
      sql(f"select count(*) from sync.change_log_archive where server_seq = {old[0]} and facility_id = '{FAC}' "
          f"and change_type = 'PERSISTED' and revision = 1 and archived_at > created_at") == "1")

print("\nNo device misses a change")
status, got_b, cursor_b = pull_all(phone_b, cursor=old[1])
seen_by_b = set(seen_b) | set(seen_b2) | {c["seq"] for c in got_b}
check("phone B resumes from its cursor and receives every row after it", set(everything[2:]) <= seen_by_b,
      sorted(set(everything[2:]) - seen_by_b))
pull_page(phone_b, cursor_b, 1)
summary, _ = run_cycle()
check("once phone B has acknowledged them, the old rows are archived too",
      in_archive(old) == 6 and in_log(old) == 0)
check("the recent row still waits for the margin", in_log([recent]) == 1)
violations = sql("""
    select count(*) from sync.change_log_archive a
    join clinical.device d on d.facility_id = a.facility_id and d.revoked_at is null and d.registered_at < a.archived_at
    left join sync.device_cursor dc on dc.device_id = d.device_id
    where coalesce(dc.last_seq, 0) < a.server_seq""")
check("whole database: no archived row is ahead of the cursor of any active device that existed when it was archived",
      violations == "0", violations)

print("\nOutbox cleanup")
topic = f"hk-test-{suffix}"
sql(f"""insert into messaging.outbox (topic, msg_key, payload, created_at, published_at) values
        ('{topic}', 'old', '{{}}', now() - interval '3 hours', now() - interval '2 hours'),
        ('{topic}', 'new', '{{}}', now() - interval '5 minutes', now() - interval '5 minutes')""")
summary, _ = run_cycle()
keys = sql(f"select string_agg(msg_key, ',') from messaging.outbox where topic = '{topic}'")
check("a row published more than an hour ago is deleted; a recent one is kept", keys == "new", keys)
check("no unpublished row was ever deleted (relay still owns them)",
      sql("select count(*) from messaging.outbox where published_at is null and created_at < now() - interval '1 minute'") == "0")
check("no published row older than the retention remains",
      sql("select count(*) from messaging.outbox where published_at < now() - interval '1 hour'") == "0")

print("\nInbox retention")
consumer = f"hk-test-{suffix}"
sql(f"""insert into messaging.inbox (consumer_name, event_id, processed_at) values
        ('{consumer}', '{uuid7()}', now() - interval '9 days'),
        ('{consumer}', '{uuid7()}', now() - interval '7 days')""")
summary, _ = run_cycle()
check("inbox retention covers Kafka's 7-day topic retention plus a day", summary.get("inbox_retention") == "8.00:00:00",
      summary)
ages = sql(f"select string_agg(extract(day from now() - processed_at)::int::text, ',') from messaging.inbox "
           f"where consumer_name = '{consumer}'")
check("a 9-day-old inbox row is deleted; a 7-day-old one (still redeliverable) is kept", ages == "7", ages)
sql(f"delete from messaging.inbox where consumer_name = '{consumer}'")
sql(f"delete from messaging.outbox where topic = '{topic}'")

sys.exit(t.finish())
