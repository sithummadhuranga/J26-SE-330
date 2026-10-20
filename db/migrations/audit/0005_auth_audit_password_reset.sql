-- Password reset by an admin (a clinician who forgot their password). Recorded like the other admin actions:
-- the clinician's id, the admin as actor.
ALTER TABLE audit.auth_audit DROP CONSTRAINT auth_audit_action_check;
ALTER TABLE audit.auth_audit ADD CONSTRAINT auth_audit_action_check CHECK (action IN (
    'LOGIN', 'REFRESH', 'LOGOUT', 'LOCKOUT',
    'REGISTER', 'UNLOCK', 'DEACTIVATE',
    'MFA_ENROLL', 'MFA_CONFIRM', 'MFA_RESET',
    'DEVICE_REVOKE', 'PASSWORD_RESET'));
