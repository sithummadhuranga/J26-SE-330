-- Refresh grace (ADR 0006): a phone on a weak network can lose the answer to a refresh after the server already
-- rotated the session. replaced_by records which row a refresh issued, so for a short time the old refresh token
-- can still be traded once more, as long as the row it was replaced by has never been used. Rows ended by sign-out,
-- deactivation or revocation have no replaced_by and get no grace.
-- Additive and nullable, so the previous service version is unaffected (§9.2).
ALTER TABLE clinical.clinician_session ADD COLUMN replaced_by uuid;
