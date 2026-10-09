-- Device revocation (architecture §12). Column-level, like the rest of grants/.
--   * identity-service: an admin revokes a device (sets revoked_at, never clears or deletes it).
--   * sync-gateway: refuses a revoked device's access tokens before they expire, so it reads that one column.
GRANT UPDATE (revoked_at) ON clinical.device TO identity_svc;
GRANT SELECT (device_id, revoked_at) ON clinical.device TO gateway_svc;
