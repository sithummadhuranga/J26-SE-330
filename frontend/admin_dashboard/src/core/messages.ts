import { ApiError, TransportError } from './api';
import { minPasswordLength } from './models';
import { NeedsSignIn } from './session';

/** Turns backend errors into plain sentences an admin can act on. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code && error.code in codes) return codes[error.code]!;
    const s = error.status;
    if (s === 400) return 'Some of the details are not valid. Check the form and try again.';
    if (s === 401) return 'Your session has ended. Please sign in again.';
    if (s === 403) return 'Your account does not have permission to do this.';
    if (s === 404) return 'This item could not be found. It may have been changed by someone else; refresh the list.';
    if (s === 409) return 'This conflicts with a change made in the meantime. Refresh the list and try again.';
    if (s === 413) return 'That request is too large to send.';
    if (s === 429) return 'Too many attempts in a short time. Please wait a minute and try again.';
    if (s >= 500) return 'The server had a problem. Please try again in a moment.';
    return 'The request could not be completed. Please try again.';
  }
  if (error instanceof TransportError) return 'Cannot reach the server. Check your connection and try again.';
  if (error instanceof NeedsSignIn) return codes[error.reason] ?? 'Your session has ended. Please sign in again.';
  return 'Something went wrong. Please try again.';
}

const actionLabels: Record<string, string> = {
  LOGIN: 'Sign-in',
  REFRESH: 'Session renewed',
  LOGOUT: 'Sign-out',
  LOCKOUT: 'Account locked',
  REGISTER: 'Account created',
  UNLOCK: 'Account unlocked',
  DEACTIVATE: 'Account deactivated',
  REACTIVATE: 'Account reactivated',
  MFA_ENROLL: 'Two-step setup started',
  MFA_CONFIRM: 'Two-step turned on',
  MFA_RESET: 'Two-step reset',
  DEVICE_REVOKE: 'Device revoked',
  PASSWORD_RESET: 'Password reset',
  PASSWORD_CHANGE: 'Own password chosen',
  PROFILE_UPDATE: 'Profile updated',
};

/** An audit action as an admin would say it. */
export const auditActionLabel = (action: string): string => actionLabels[action] ?? 'Other';

const reasonLabels: Record<string, string> = {
  INVALID_CREDENTIALS: 'Wrong password',
  CREDENTIAL_LOCKED: 'Locked',
  MFA_REQUIRED: 'Code requested',
  PASSWORD_CHANGE_REQUIRED: 'New password requested',
  INVALID_TOTP: 'Wrong code',
  CLIENT_NOT_ALLOWED: 'Not an admin',
  DEVICE_NOT_ALLOWED: 'Device revoked',
  INVALID_REFRESH_TOKEN: 'Session ended',
};

/** Why an audited attempt did not succeed, in a word or two. */
export const auditReasonLabel = (reason: string | null): string => (reason && reasonLabels[reason]) || 'Failed';

export const codes: Record<string, string> = {
  // Sign-in. Wrong passwords, codes and locked accounts are explained on the identity service's login page.
  invalid_grant: 'Your session has ended. Please sign in again.',
  INVALID_REFRESH_TOKEN: 'Your session has ended. Please sign in again.',
  STATE_MISMATCH: 'That sign-in did not start in this tab. Please sign in again.',
  access_denied: 'Sign-in was cancelled. Please try again.',
  login_required: 'Please sign in again.',
  CODE_REFUSED: 'Sign-in could not be completed. Please try again.',
  NO_CODE: 'Sign-in could not be completed. Please try again.',
  // Clinicians
  USERNAME_TAKEN: 'That username is already in use. Choose another one.',
  INVALID_USERNAME:
    'Usernames are 3 to 64 characters: lowercase letters, digits, dots, dashes and underscores, ' +
    'starting with a letter or digit.',
  PASSWORD_TOO_SHORT: `The password must be at least ${minPasswordLength} characters.`,
  INVALID_ROLE: 'Choose a role: nurse, wound specialist or admin.',
  MISSING_FULL_NAME: 'Enter the full name.',
  FULL_NAME_TOO_LONG: 'The full name can be at most 120 characters.',
  UNKNOWN_FACILITY: 'Your facility is not registered. Contact the system administrator.',
  CANNOT_DEACTIVATE_SELF: 'You cannot deactivate your own account. Ask another admin.',
  ALREADY_ACTIVE: 'This account is already active. Refresh the list.',
  CANNOT_RESET_OWN_PASSWORD: 'You cannot reset your own password here. Ask another admin.',
  NOT_FOUND: 'This item could not be found. It may have been changed by someone else; refresh the list.',
  // Two-step sign-in
  INVALID_TOTP: 'That code is not right. Check the time on your phone and enter the newest code.',
  MFA_ALREADY_ENABLED: 'Two-step sign-in is already on for your account.',
  MFA_NOT_ENROLLED: 'Setup was not started or has expired. Start again.',
  // Devices
  DEVICE_ALREADY_REVOKED: 'This device is already revoked.',
};
