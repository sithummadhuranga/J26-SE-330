-- Web sessions for the admin dashboard (ADR 0005). A session now records which client it was issued to.
-- Mobile sessions are bound to a registered device as before; admin-dashboard sessions run in a browser,
-- which is not a device, so they have none. Additive: existing rows become 'mobile', and the previous
-- service version keeps working because it always supplies a device_id (§9.2).
ALTER TABLE clinical.clinician_session
    ADD COLUMN client_id text NOT NULL DEFAULT 'mobile'
        CONSTRAINT ck_clinician_session_client CHECK (client_id IN ('mobile', 'admin-dashboard')),
    ALTER COLUMN device_id DROP NOT NULL,
    ADD CONSTRAINT ck_clinician_session_device
        CHECK ((client_id = 'mobile') = (device_id IS NOT NULL));
