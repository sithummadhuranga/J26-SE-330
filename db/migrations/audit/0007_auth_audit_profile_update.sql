-- PROFILE_UPDATE: a clinician changes their own full name (plan: profile management). The clinician is also the actor.
ALTER TABLE audit.auth_audit DROP CONSTRAINT auth_audit_action_check;
ALTER TABLE audit.auth_audit ADD CONSTRAINT auth_audit_action_check CHECK (action IN (
    'LOGIN', 'REFRESH', 'LOGOUT', 'LOCKOUT',
    'REGISTER', 'UNLOCK', 'DEACTIVATE', 'REACTIVATE',
    'MFA_ENROLL', 'MFA_CONFIRM', 'MFA_RESET',
    'DEVICE_REVOKE', 'PASSWORD_RESET', 'PASSWORD_CHANGE',
    'PROFILE_UPDATE'));
