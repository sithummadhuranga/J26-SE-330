-- OpenID Connect code redemption (ADR 0006): only the identity service records and purges redeemed codes.
GRANT SELECT, INSERT, DELETE ON clinical.oidc_code_redemption TO identity_svc;
