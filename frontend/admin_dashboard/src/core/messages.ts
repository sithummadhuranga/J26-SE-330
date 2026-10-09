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
  if (error instanceof NeedsSignIn) return 'Your session has ended. Please sign in again.';
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
  MFA_ENROLL: 'Two-step setup started',
  MFA_CONFIRM: 'Two-step turned on',
  MFA_RESET: 'Two-step reset',
  DEVICE_REVOKE: 'Device revoked',
};

/** An audit action as an admin would say it. */
export const auditActionLabel = (action: string): string => actionLabels[action] ?? 'Other';

const reasonLabels: Record<string, string> = {
  INVALID_CREDENTIALS: 'Wrong password',
  CREDENTIAL_LOCKED: 'Locked',
  MFA_REQUIRED: 'Code requested',
  INVALID_TOTP: 'Wrong code',
  CLIENT_NOT_ALLOWED: 'Not an admin',
  DEVICE_NOT_ALLOWED: 'Device revoked',
  INVALID_REFRESH_TOKEN: 'Session ended',
};

/** Why an audited attempt did not succeed, in a word or two. */
export const auditReasonLabel = (reason: string | null): string => (reason && reasonLabels[reason]) || 'Failed';

export const codes: Record<string, string> = {
  // Sign-in
  INVALID_CREDENTIALS: 'The username or password is incorrect.',
  CREDENTIAL_LOCKED: 'This account is locked after too many failed attempts. Another admin can unlock it.',
  MFA_REQUIRED: 'Enter the 6-digit code from your authenticator app.',
  INVALID_TOTP: 'That code is not valid. Check your authenticator app and try again.',
  CLIENT_NOT_ALLOWED: 'Only admins can sign in to the dashboard. Nurses and wound specialists use the mobile app.',
  MISSING_FIELDS: 'Enter your username and password.',
  DEVICE_NOT_EXPECTED: 'This sign-in is not allowed from the dashboard.',
  DEVICE_NOT_ALLOWED: 'This device has been revoked and can no longer be used.',
  UNKNOWN_CLIENT: 'This version of the dashboard is not recognized. Reload the page.',
  INVALID_REFRESH_TOKEN: 'Your session has ended. Please sign in again.',
  // Clinicians
  USERNAME_TAKEN: 'That username is already in use. Choose another one.',
  INVALID_USERNAME:
    'Usernames are 3 to 64 characters: lowercase letters, digits, dots, dashes and underscores, ' +
    'starting with a letter or digit.',
  PASSWORD_TOO_SHORT: `The password must be at least ${minPasswordLength} characters.`,
  INVALID_ROLE: 'Choose a role: nurse, wound specialist or admin.',
  MISSING_FULL_NAME: 'Enter the full name.',
  UNKNOWN_FACILITY: 'Your facility is not registered. Contact the system administrator.',
  CANNOT_DEACTIVATE_SELF: 'You cannot deactivate your own account. Ask another admin.',
  NOT_FOUND: 'This item could not be found. It may have been changed by someone else; refresh the list.',
  // Devices
  DEVICE_ALREADY_REVOKED: 'This device is already revoked.',
};
