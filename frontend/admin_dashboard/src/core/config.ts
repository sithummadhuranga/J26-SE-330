// Build-time settings; e.g. VITE_API_BASE_URL=http://localhost:8080 npm run build.
export const apiBaseUrl: string = import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080';

// The client the identity service issues dashboard sessions to: role admin only, no device (ADR 0005).
export const clientId = 'admin-dashboard';
