import { clientId } from './config';
import type { AuthAuditEntry, ClinicianSummary, DeviceSummary, MfaEnrollment, MyProfile, TokenPair } from './models';

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

  // ---- auth: OpenID Connect (ADR 0006) ----

  /** The identity service's sign-in page for this request; the browser goes there, never this code. */
  authorizeUrl(params: { redirectUri: string; state: string; codeChallenge: string }): string {
    const query = new URLSearchParams({
      response_type: 'code',
      client_id: clientId,
      redirect_uri: params.redirectUri,
      scope: 'openid profile offline_access',
      state: params.state,
      code_challenge: params.codeChallenge,
      code_challenge_method: 'S256',
    });
    return `${this.base}/connect/authorize?${query.toString()}`;
  }

  /** Swaps the code from /callback for tokens. Throws ApiError with the OAuth error, e.g. invalid_grant. */
  exchangeCode(code: string, codeVerifier: string, redirectUri: string): Promise<TokenPair> {
    return this.token({ grant_type: 'authorization_code', code, code_verifier: codeVerifier, redirect_uri: redirectUri });
  }

  /** Rotates both tokens; the old refresh token stops working. Throws ApiError invalid_grant once the session is over. */
  refresh(refreshToken: string): Promise<TokenPair> {
    return this.token({ grant_type: 'refresh_token', refresh_token: refreshToken });
  }

  /** Ends the session on the server. */
  async logout(refreshToken: string): Promise<void> {
    await this.form('connect/revocation', { token: refreshToken, token_type_hint: 'refresh_token' });
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

  /** Undoes a deactivation. Throws ApiError 409 ALREADY_ACTIVE. */
  async reactivate(token: string, username: string): Promise<void> {
    await this.post(`v1/admin/clinicians/${enc(username)}/reactivate`, null, token);
  }

  async resetMfa(token: string, username: string): Promise<void> {
    await this.post(`v1/admin/clinicians/${enc(username)}/reset-mfa`, null, token);
  }

  /** Also clears a lockout and signs the clinician out everywhere. Throws ApiError 400 PASSWORD_TOO_SHORT, 409 CANNOT_RESET_OWN_PASSWORD. */
  async resetPassword(token: string, username: string, password: string): Promise<void> {
    await this.post(`v1/admin/clinicians/${enc(username)}/reset-password`, { password }, token);
  }

  // ---- own account ----

  /** Your own profile; the account is the one in the token. Throws ApiError 404 once the account is deactivated. */
  me(token: string): Promise<MyProfile> {
    return this.get('v1/me', token) as Promise<MyProfile>;
  }

  /**
   * Changes your own password; on success the server ends every session, this one included. Throws ApiError 400
   * INVALID_CREDENTIALS, PASSWORD_TOO_SHORT, PASSWORD_UNCHANGED, or 409 CREDENTIAL_LOCKED.
   */
  async changePassword(token: string, currentPassword: string, newPassword: string): Promise<void> {
    await this.post('v1/me/password', { currentPassword, newPassword }, token);
  }

  /** Changes your own full name; answers the updated profile. Throws ApiError 400 MISSING_FULL_NAME, FULL_NAME_TOO_LONG. */
  updateProfile(token: string, fullName: string): Promise<MyProfile> {
    return this.send('v1/me', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
      body: JSON.stringify({ fullName }),
    }) as Promise<MyProfile>;
  }

  // ---- own account: two-step sign-in ----

  /** Starts two-step setup with a new secret; MFA turns on only after confirmMfa. Throws ApiError 409 MFA_ALREADY_ENABLED. */
  mfaEnroll(token: string): Promise<MfaEnrollment> {
    return this.post('v1/auth/mfa/enroll', null, token) as Promise<MfaEnrollment>;
  }

  /** Throws ApiError 400 INVALID_TOTP, or 409 MFA_NOT_ENROLLED, MFA_ALREADY_ENABLED. */
  async confirmMfa(token: string, code: string): Promise<void> {
    await this.post('v1/auth/mfa/confirm', { code }, token);
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

  private async token(fields: Record<string, string>): Promise<TokenPair> {
    const body = await this.form('connect/token', fields);
    if (!isRecord(body) || typeof body.access_token !== 'string' || typeof body.refresh_token !== 'string')
      throw new ApiError(502, 'invalid_token_response');
    return {
      accessToken: body.access_token,
      refreshToken: body.refresh_token,
      expiresIn: typeof body.expires_in === 'number' ? body.expires_in : 0,
      idToken: typeof body.id_token === 'string' ? body.id_token : null,
    };
  }

  /** OAuth endpoints take form fields and answer {error, error_description}. */
  private form(path: string, fields: Record<string, string>): Promise<unknown> {
    return this.send(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ client_id: clientId, ...fields }).toString(),
    });
  }

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
    // Our API sends {code}; OAuth endpoints send {error}.
    const code = !isRecord(body) ? null : typeof body.code === 'string' ? body.code : typeof body.error === 'string' ? body.error : null;
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
