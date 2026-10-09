-- TOTP multi-factor login (architecture §7.3, §12).
-- mfa_secret_encrypted (from 0005) holds the secret encrypted by the gateway with AES-GCM; the key lives
-- outside the database, with the JWT signing key. It is only trusted once mfa_enabled is true, i.e. after
-- the clinician has confirmed a first code.
ALTER TABLE clinical.clinician_credential
    ADD COLUMN mfa_enabled        boolean NOT NULL DEFAULT false,
    ADD COLUMN mfa_last_used_step bigint;   -- last accepted 30-second step; stops a code being replayed
