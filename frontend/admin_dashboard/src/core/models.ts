// The shapes of what the identity service sends back for auth and admin calls
// (contracts/auth.schema.json 2.0 and /v1/admin/*).

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
  /** Seconds until the access token expires. */
  expiresIn: number;
  /** Who signed in (preferred_username), only used for display. It's null if the server didn't send one. */
  idToken: string | null;
}

/** The roles a clinician can be registered with. */
export const clinicianRoles = ['nurse', 'wound_specialist', 'admin'] as const;
export type ClinicianRole = (typeof clinicianRoles)[number];

/** How each role is shown on screen. The stored value stays as it is above. */
export function roleLabel(role: string): string {
  switch (role) {
    case 'nurse':
      return 'Nurse';
    case 'wound_specialist':
      return 'Wound specialist';
    case 'admin':
      return 'Admin';
    default:
      return role;
  }
}

/** The server's minimum password length. */
export const minPasswordLength = 8;

/** The server's rule for usernames: 3 to 64 lower-case letters, digits, dots, dashes or underscores. */
export const usernamePattern = /^[a-z0-9][a-z0-9._-]{2,63}$/;

export interface ClinicianSummary {
  clinicianId: string;
  username: string;
  fullName: string;
  role: string;
  active: boolean;
  locked: boolean;
  mfaEnabled: boolean;
  createdAt: string;
  /** An admin set the current password, when registering or resetting. The clinician replaces it next sign-in. */
  mustChangePassword: boolean;
}

/** The signed-in clinician's own account (GET /v1/me). */
export interface MyProfile {
  username: string;
  fullName: string;
  role: string;
  facilityId: string;
  mfaEnabled: boolean;
  mustChangePassword: boolean;
  createdAt: string;
  /** Null before the first sign-in is recorded. */
  lastSignInAt: string | null;
}

/** One of your sign-ins that is still going (GET /v1/me/sessions). */
export interface MySession {
  sessionId: string;
  /** "admin-dashboard" or "mobile". */
  client: string;
  /** The phone, for mobile sessions. It's null for the dashboard. */
  deviceId: string | null;
  signedInAt: string;
  /** The latest sign-in or token refresh in this session. */
  lastUsedAt: string;
  expiresAt: string;
}

/** Two-step setup that has been started: the secret to type in, and the otpauth:// URI inside the QR code. */
export interface MfaEnrollment {
  secret: string;
  otpauthUri: string;
}

export interface DeviceSummary {
  deviceId: string;
  registeredAt: string;
  revokedAt: string | null;
  activeSessions: number;
  lastSeenAt: string | null;
  lastUsername: string | null;
}

export interface AuthAuditEntry {
  id: number;
  recordedAt: string;
  action: string;
  success: boolean;
  username: string;
  reasonCode: string | null;
  deviceId: string | null;
  actorUsername: string | null;
}
