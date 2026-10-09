-- Device revocation (architecture §12: "devices are registered and can be revoked"). An admin revoking a phone is
-- recorded like the other admin actions: device_id set, clinician_id empty, the admin as actor.
ALTER TABLE audit.auth_audit DROP CONSTRAINT auth_audit_action_check;
ALTER TABLE audit.auth_audit ADD CONSTRAINT auth_audit_action_check CHECK (action IN (
    'LOGIN', 'REFRESH', 'LOGOUT', 'LOCKOUT',
    'REGISTER', 'UNLOCK', 'DEACTIVATE',
    'MFA_ENROLL', 'MFA_CONFIRM', 'MFA_RESET',
    'DEVICE_REVOKE'));
