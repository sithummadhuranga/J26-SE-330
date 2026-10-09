-- housekeeping (architecture §9.4). Deletes only what is short-lived by design, reads only the columns its retention
-- rules look at. It never sees payloads, clinical data or credentials.
GRANT USAGE ON SCHEMA sync, messaging, clinical TO housekeeping_svc;
GRANT SELECT ON sync.schema_migrations TO housekeeping_svc;                -- startup schema-version check (§9.2)

-- A2 outbox cleanup: published rows only (the rule needs published_at; the relay owns unpublished ones).
GRANT SELECT (outbox_id, published_at), DELETE ON messaging.outbox TO housekeeping_svc;
-- A4 inbox retention: rows older than the longest Kafka redelivery window.
GRANT SELECT (consumer_name, event_id, processed_at), DELETE ON messaging.inbox TO housekeeping_svc;
-- A3 change-log archival: once every device cursor of the facility has passed a row, it moves to the archive.
GRANT SELECT, DELETE ON sync.change_log TO housekeeping_svc;
GRANT INSERT ON sync.change_log_archive TO housekeeping_svc;
GRANT SELECT ON sync.device_cursor TO housekeeping_svc;
GRANT SELECT (device_id, facility_id, revoked_at) ON clinical.device TO housekeeping_svc;
GRANT SELECT (facility_id) ON clinical.facility TO housekeeping_svc;
