-- OpenID Connect authorization codes are single use (ADR 0006). The identity service runs OpenIddict without a
-- token store, so it records each code it redeems here: a second redemption hits the primary key and is refused.
-- A row is only needed until its code would have expired anyway (5 minutes); the identity service deletes
-- expired rows as it goes. New table only, so the previous service version is unaffected (§9.2).
CREATE TABLE clinical.oidc_code_redemption (
    code_id     uuid PRIMARY KEY,
    redeemed_at timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL
);

CREATE INDEX ix_oidc_code_redemption_expires ON clinical.oidc_code_redemption (expires_at);
