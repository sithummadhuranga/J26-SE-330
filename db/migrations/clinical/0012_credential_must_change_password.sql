-- A password an admin chose (registration or reset) is shared in person, so the clinician replaces it the first time
-- they sign in on the login page. Existing accounts are not asked. Additive with a default, so the previous service
-- version is unaffected (§9.2).
ALTER TABLE clinical.clinician_credential ADD COLUMN must_change_password boolean NOT NULL DEFAULT false;
