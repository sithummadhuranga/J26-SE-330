-- REACTIVATE: an admin undoes a deactivation. PASSWORD_CHANGE: a clinician replaces the password an admin chose; the
-- login page's prompt is recorded as a failed PASSWORD_CHANGE with reason PASSWORD_CHANGE_REQUIRED, so it never
-- counts as a failed login.
ALTER TABLE audit.auth_audit DROP CONSTRAINT auth_audit_action_check;
ALTER TABLE audit.auth_audit ADD CONSTRAINT auth_audit_action_check CHECK (action IN (
    'LOGIN', 'REFRESH', 'LOGOUT', 'LOCKOUT',
    'REGISTER', 'UNLOCK', 'DEACTIVATE', 'REACTIVATE',
    'MFA_ENROLL', 'MFA_CONFIRM', 'MFA_RESET',
    'DEVICE_REVOKE', 'PASSWORD_RESET', 'PASSWORD_CHANGE'));
