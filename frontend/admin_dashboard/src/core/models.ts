// Shapes of the identity service's auth and admin answers (contracts/auth.schema.json, /v1/admin/*).

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
  /** Seconds until the access token expires. */
  expiresIn: number;
}

/** The roles a clinician can be registered with. */
export const clinicianRoles = ['nurse', 'wound_specialist', 'admin'] as const;
export type ClinicianRole = (typeof clinicianRoles)[number];

/** How a role reads on screen; the stored value stays as above. */
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
export const minPasswordLength = 12;

/** The server's username rule: 3–64 lower-case letters, digits, dots, dashes or underscores. */
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
