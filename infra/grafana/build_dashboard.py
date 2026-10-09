"""Generates the Grafana sync-pipeline dashboard JSON; edit this file, not the JSON, then run it."""
import json
import os

DS = {"type": "prometheus", "uid": "prometheus"}
panels, y = [], 0


def ts(title, exprs, unit="short", w=12, h=8, x=0, desc=""):
    global y
    panels.append({
        "type": "timeseries", "title": title, "description": desc, "datasource": DS,
        "gridPos": {"x": x, "y": y, "w": w, "h": h},
        "fieldConfig": {"defaults": {"unit": unit, "custom": {"lineWidth": 2, "fillOpacity": 10}}, "overrides": []},
        "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi"}},
        "targets": [{"datasource": DS, "expr": e, "legendFormat": l, "refId": chr(65 + i)} for i, (e, l) in enumerate(exprs)],
    })


def stat(title, expr, unit="short", x=0, w=6, desc="", color="green", nearest=None):
    expr = f"round({expr}, {nearest})" if nearest else f"round({expr})"
    panels.append({
        "type": "stat", "title": title, "description": desc, "datasource": DS,
        "gridPos": {"x": x, "y": y, "w": w, "h": 4},
        "fieldConfig": {"defaults": {"unit": unit, "color": {"mode": "fixed", "fixedColor": color}}, "overrides": []},
        "options": {"reduceOptions": {"calcs": ["lastNotNull"]}, "colorMode": "value", "graphMode": "none"},
        "targets": [{"datasource": DS, "expr": expr, "refId": "A", "instant": True}],
    })


def row(title):
    global y
    panels.append({"type": "row", "title": title, "collapsed": False, "gridPos": {"x": 0, "y": y, "w": 24, "h": 1}})
    y += 1


row("Guarantees over the selected range (§11)")
stat("Events accepted", 'sum(increase(sync_events_pushed_total{result="ACCEPTED"}[$__range]))', x=0,
     desc="Per-event ACCEPTED push results at the gateway")
stat("Resends answered DUPLICATE", 'sum(increase(sync_events_pushed_total{result="DUPLICATE"}[$__range])) or vector(0)',
     x=6, color="blue", desc="Repeats the gateway absorbed: never produced again")
stat("Redeliveries absorbed by the persister", 'sum(increase(sync_events_persisted_total{outcome="deduplicated"}[$__range])) or vector(0)',
     x=12, color="blue", desc="DEDUPLICATED: a repeat that reached the persister and stored nothing")
stat("Sent to the DLQ", 'sum(increase(sync_orchestration_outcomes_total{kind="DeadLetter"}[$__range])) + '
     'sum(increase(sync_events_persisted_total{outcome="dead_letter"}[$__range])) or vector(0)',
     x=18, color="red", desc="Contract errors, unparseable messages, retries exhausted")
y += 4

row("Kafka consumer lag (§13: rises, then drains)")
ts("Consumer lag by group", [('sum by (consumergroup) (kafka_consumergroup_lag{consumergroup=~"persister|orchestrator|orchestrator-retry"})',
                              "{{consumergroup}}")], w=24,
   desc="Messages waiting per consumer group (kafka-exporter)")
y += 8

row("Throughput")
ts("Push results / s", [("sum by (result) (rate(sync_events_pushed_total[1m]))", "{{result}}")], unit="reqps", x=0)
ts("Persister outcomes / s", [("sum by (outcome) (rate(sync_events_persisted_total[1m]))", "{{outcome}}")], unit="reqps", x=12)
y += 8
ts("Orchestrator outcomes / s", [("sum by (kind) (rate(sync_orchestration_outcomes_total[1m]))", "{{kind}}")], unit="reqps", x=0)
ts("Outbox published / s", [("sum by (topic) (rate(sync_outbox_published_total[1m]))", "{{topic}}")], unit="reqps", x=12)
y += 8

row("Latency")
ts("Push request at the gateway (p50 / p95)", [
    ('histogram_quantile(0.5, sum by (le) (rate(http_server_request_duration_seconds_bucket{service_name="sync-gateway",http_route="/v1/sync/push"}[1m])))', "p50"),
    ('histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{service_name="sync-gateway",http_route="/v1/sync/push"}[1m])))', "p95")],
   unit="s", x=0, desc="What the device waits for: validate + produce with acks=all")
ts("Persister transaction (p50 / p95)", [
    ("histogram_quantile(0.5, sum by (le) (rate(sync_persist_duration_seconds_bucket[1m])))", "p50"),
    ("histogram_quantile(0.95, sum by (le) (rate(sync_persist_duration_seconds_bucket[1m])))", "p95")], unit="s", x=12)
y += 8
ts("Orchestrator workflow run (p50 / p95)", [
    ("histogram_quantile(0.5, sum by (le) (rate(sync_orchestration_duration_seconds_bucket[1m])))", "p50"),
    ("histogram_quantile(0.95, sum by (le) (rate(sync_orchestration_duration_seconds_bucket[1m])))", "p95")], unit="s", x=0)
ts("Recommendation Service call (p50 / p95)", [
    ('histogram_quantile(0.5, sum by (le) (rate(http_client_request_duration_seconds_bucket{service_name="orchestrator"}[1m])))', "p50"),
    ('histogram_quantile(0.95, sum by (le) (rate(http_client_request_duration_seconds_bucket{service_name="orchestrator"}[1m])))', "p95")],
   unit="s", x=12, desc="HTTP client duration from the orchestrator")
y += 8

row("HTTP by service")
ts("Requests / s by service and route", [
    ('sum by (service_name, http_route) (rate(http_server_request_duration_seconds_count{http_route!="/health"}[1m]))',
     "{{service_name}} {{http_route}}")], unit="reqps", x=0)
ts("5xx responses / s by service", [
    ('sum by (service_name) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[1m])) or vector(0)',
     "{{service_name}}")], unit="reqps", x=12)
y += 8

row("Auth health (§13: login failure rate, lockouts per day, average session lifetime)")
stat("Login failure rate", '(sum(increase(auth_events_total{action="LOGIN",success="false",reason!="MFA_REQUIRED"}[$__range])) + sum(increase(auth_events_total{action="LOCKOUT"}[$__range]) or vector(0))) / sum(increase(auth_events_total{action=~"LOGIN|LOCKOUT",reason!="MFA_REQUIRED"}[$__range]))', unit="percentunit", nearest=0.001, x=0, color="orange",
     desc="Failed login attempts / attempts over the range. A LOCKOUT is the failed attempt that locked the account; "
          "MFA_REQUIRED (the server asking for a code) is not an attempt")
stat("Lockouts per day", 'sum(increase(auth_events_total{action="LOCKOUT"}[1d])) or vector(0)', x=6, color="red",
     desc="Accounts locked after 5 failed attempts, over the last 24 hours")
stat("Average session lifetime (ended, 24 h)", 'max(auth_session_lifetime_seconds{state="ended",client="mobile"})',
     unit="s", x=12, color="blue",
     desc="Mobile sessions (a login and its refreshes) that ended by logout or revocation in the last 24 h: login to end. "
          "Sampled from the database once a minute")
stat("Active sessions", 'sum(max by (client) (auth_sessions_active))', x=18,
     desc="Logins whose session is still valid, all clients")
y += 4
ts("Login attempts / s by result", [
    ('sum by (reason) (rate(auth_events_total{action=~"LOGIN|LOCKOUT",success="false",reason!="MFA_REQUIRED"}[5m]))',
     "failed: {{reason}}"),
    ('sum(rate(auth_events_total{action="LOGIN",success="true"}[5m]))', "succeeded")], unit="reqps", x=0,
   desc="INVALID_CREDENTIALS, INVALID_TOTP, CREDENTIAL_LOCKED, DEVICE_NOT_ALLOWED, CLIENT_NOT_ALLOWED")
ts("Average session lifetime by client", [
    ('max by (client, state) (auth_session_lifetime_seconds)', "{{client}} {{state}}")], unit="s", x=12,
   desc="active: average age of valid sessions; ended: login to logout or revocation, sessions ended in 24 h")
y += 8

dashboard = {
    "uid": "sync-pipeline", "title": "Sync pipeline (§13)", "tags": ["melanin-wound-cdss"],
    "timezone": "browser", "schemaVersion": 39, "version": 1, "refresh": "5s",
    "time": {"from": "now-30m", "to": "now"},
    "description": "Consumer lag, throughput, duplicates absorbed and latency for the event-driven sync pipeline "
                   "(plan phase 11). Traces: Jaeger at http://localhost:16686.",
    "panels": panels,
}
with open(os.path.join(os.path.dirname(__file__), "dashboards", "sync-pipeline.json"), "w", encoding="utf-8") as f:
    json.dump(dashboard, f, indent=2)
    f.write("\n")
print(f"Wrote {len(panels)} panels to infra/grafana/dashboards/sync-pipeline.json")
