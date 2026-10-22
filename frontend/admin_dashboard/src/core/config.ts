// Settings baked in at build time, for example VITE_API_BASE_URL=http://localhost:8080 npm run build.
export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080';

// The client id the identity service gives dashboard sessions to. Only admins can use it, and there's no device
// involved (ADR 0005, 0006).
export const clientId = 'admin-dashboard';

/** Where the identity service sends the browser back with the code. It has to match what's registered there exactly. */
export const redirectUri: string =
  import.meta.env.VITE_REDIRECT_URI || `${globalThis.location?.origin ?? 'http://localhost:3001'}/callback`;
