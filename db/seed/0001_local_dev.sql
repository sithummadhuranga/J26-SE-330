-- Local development only. Never run in any shared environment.
INSERT INTO clinical.facility (facility_id, name) VALUES ('fac-001', 'Demo District Hospital')
ON CONFLICT DO NOTHING;

INSERT INTO clinical.device (device_id, facility_id) VALUES ('dev-a41c', 'fac-001')
ON CONFLICT DO NOTHING;
