// Build-time settings; e.g. VITE_API_BASE_URL=http://localhost:8080 npm run build.
export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080';

// The client the identity service issues dashboard sessions to: role admin only, no device (ADR 0005, 0006).
export const clientId = 'admin-dashboard';

/** Where the identity service sends the browser back with the code; must be registered there exactly. */
export const redirectUri: string =
  import.meta.env.VITE_REDIRECT_URI || `${globalThis.location?.origin ?? 'http://localhost:3001'}/callback`;
