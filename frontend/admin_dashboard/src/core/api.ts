import { clientId } from './config';
import type { AuthAuditEntry, ClinicianSummary, DeviceSummary, TokenPair } from './models';

/** No HTTP answer at all: gateway down, CORS refused, timeout. */
export class TransportError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'TransportError';
  }
}

/** An HTTP error answer, with the reason code the backend sends (`{"code": "..."}`). */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string | null;

  constructor(status: number, code: string | null = null) {
    super(`ApiError(${status}${code ? ` ${code}` : ''})`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

export const requestTimeoutMs = 20_000;

const enc = encodeURIComponent;

/** The dashboard's auth and admin API calls, all through the API gateway. */
export class AdminApi {
  private readonly base: string;
  private readonly fetchFn: typeof fetch;

  constructor(baseUrl: string, fetchFn: typeof fetch = (...args) => fetch(...args)) {
    this.base = baseUrl.replace(/\/+$/, '');
    this.fetchFn = fetchFn;
  }

  // ---- auth ----

  /** Throws ApiError 401 with MFA_REQUIRED, INVALID_TOTP, INVALID_CREDENTIALS, CREDENTIAL_LOCKED or CLIENT_NOT_ALLOWED. */
  login(username: string, password: string, totp?: string): Promise<TokenPair> {
    return this.post('v1/auth/login', { username, password, clientId, ...(totp ? { totp } : {}) }) as Promise<TokenPair>;
  }

  refresh(refreshToken: string): Promise<TokenPair> {
    return this.post('v1/auth/refresh', { refreshToken }) as Promise<TokenPair>;
  }

  async logout(refreshToken: string): Promise<void> {
    await this.post('v1/auth/logout', { refreshToken });
  }

  // ---- clinicians ----

  clinicians(token: string): Promise<ClinicianSummary[]> {
    return this.get('v1/admin/clinicians', token) as Promise<ClinicianSummary[]>;
  }

  /** Throws ApiError 409 USERNAME_TAKEN, or 400 INVALID_USERNAME, PASSWORD_TOO_SHORT, INVALID_ROLE, ... */
  async registerClinician(
    token: string,
    clinician: { username: string; password: string; fullName: string; role: string },
  ): Promise<void> {
    await this.post('v1/admin/clinicians', clinician, token);
  }

  async unlock(token: string, username: string): Promise<void> {
    await this.post(`v1/admin/clinicians/${enc(username)}/unlock`, null, token);
  }

  /** Throws ApiError 409 CANNOT_DEACTIVATE_SELF. */
  async deactivate(token: string, username: string): Promise<void> {
    await this.post(`v1/admin/clinicians/${enc(username)}/deactivate`, null, token);
  }

  async resetMfa(token: string, username: string): Promise<void> {
    await this.post(`v1/admin/clinicians/${enc(username)}/reset-mfa`, null, token);
  }

  // ---- devices ----

  devices(token: string): Promise<DeviceSummary[]> {
    return this.get('v1/admin/devices', token) as Promise<DeviceSummary[]>;
  }

  /** Throws ApiError 409 DEVICE_ALREADY_REVOKED. */
  async revokeDevice(token: string, deviceId: string): Promise<void> {
    await this.post(`v1/admin/devices/${enc(deviceId)}/revoke`, null, token);
  }

  // ---- audit ----

  authAudit(token: string, limit = 100): Promise<AuthAuditEntry[]> {
    return this.get(`v1/admin/auth-audit?limit=${limit}`, token) as Promise<AuthAuditEntry[]>;
  }

  // ---- plumbing ----

  private get(path: string, token: string): Promise<unknown> {
    return this.send(path, { method: 'GET', headers: { Authorization: `Bearer ${token}` } });
  }

  private post(path: string, body: unknown, token?: string): Promise<unknown> {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' };
    if (token) headers.Authorization = `Bearer ${token}`;
    return this.send(path, { method: 'POST', headers, body: JSON.stringify(body ?? {}) });
  }

  /** Returns the parsed response, or throws ApiError (server said no) or TransportError (no answer). */
  private async send(path: string, init: RequestInit): Promise<unknown> {
    let response: Response;
    let text: string;
    try {
      response = await this.fetchFn(`${this.base}/${path}`, { ...init, signal: AbortSignal.timeout(requestTimeoutMs) });
      text = await response.text();
    } catch (e) {
      throw new TransportError(e instanceof Error && e.name === 'TimeoutError' ? 'timed out' : String(e));
    }
    const body = text ? tryParse(text) : null;
    if (response.ok) return body;
    const code = isRecord(body) && typeof body.code === 'string' ? body.code : null;
    throw new ApiError(response.status, code);
  }
}

function tryParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
