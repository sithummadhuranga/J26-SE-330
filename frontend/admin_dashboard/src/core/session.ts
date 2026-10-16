import { AdminApi, ApiError } from './api';
import { redirectUri as defaultRedirectUri } from './config';
import type { TokenPair } from './models';
import type { TokenStore } from './tokenStore';

/** No usable session: the admin has to sign in again. */
export class NeedsSignIn extends Error {
  readonly reason: string;

  constructor(reason: string) {
    super(`NeedsSignIn: ${reason}`);
    this.name = 'NeedsSignIn';
    this.reason = reason;
  }
}

/** The signed-in admin, read from the tokens (for display; the backend checks them). */
export interface Admin {
  id: string;
  username: string;
  role: string;
  facilityId: string;
}

const refreshKey = 'cdss_admin_refresh_v1';
const usernameKey = 'cdss_admin_username_v1';
/** The PKCE verifier and state of a sign-in in progress; they must survive the trip to the login page. */
const pendingKey = 'cdss_admin_pending_v1';

/** Refresh a little before expiry so a request never leaves with a token about to lapse. */
export const refreshMarginMs = 60_000;

/**
 * Keeps the admin signed in through the identity service's OpenID Connect login (ADR 0006): authorization code with
 * PKCE, access token in memory, refresh token in the tab's session storage.
 */
export class AuthSession {
  readonly api: AdminApi;
  private readonly store: TokenStore;
  private readonly now: () => number;
  private readonly redirectUri: string;
  private readonly listeners = new Set<() => void>();

  private accessToken: string | null = null;
  private expiresAt = 0;
  private current: Admin | null = null;
  private refreshing: Promise<string> | null = null;
  private ended = false;
  private failure: NeedsSignIn | null = null;

  constructor(api: AdminApi, store: TokenStore, now: () => number = Date.now, redirectUri = defaultRedirectUri) {
    this.api = api;
    this.store = store;
    this.now = now;
    this.redirectUri = redirectUri;
  }

  get admin(): Admin | null {
    return this.current;
  }

  get signedIn(): boolean {
    return this.current !== null;
  }

  /** Lets the screens update when someone signs in or out. */
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** True once after the server ended the session, so the sign-in screen can explain why. */
  takeSessionEndedNotice(): boolean {
    const ended = this.ended;
    this.ended = false;
    return ended;
  }

  /** Why the last sign-in did not complete, once, so the sign-in screen can say so. */
  takeSignInFailure(): NeedsSignIn | null {
    const failure = this.failure;
    this.failure = null;
    return failure;
  }

  /** After a reload, swap the refresh token for a new access token; false if there's no session. */
  async restore(): Promise<boolean> {
    if (this.store.read(refreshKey) === null) return false;
    try {
      await this.refresh();
      return true;
    } catch (e) {
      if (e instanceof NeedsSignIn) return false;
      throw e;
    }
  }

  /** Starts a sign-in: returns the identity service's login page URL for the browser to open. */
  async beginSignIn(): Promise<string> {
    const verifier = randomUrlSafe(48);
    const state = randomUrlSafe(16);
    this.store.write(pendingKey, JSON.stringify({ verifier, state }));
    return this.api.authorizeUrl({ redirectUri: this.redirectUri, state, codeChallenge: await sha256UrlSafe(verifier) });
  }

  /**
   * Finishes a sign-in from the URL the identity service sent the browser back to (/callback?code=…&state=…).
   * Throws NeedsSignIn when it is not the answer to this tab's own request, or the server refused.
   */
  async completeSignIn(callbackUrl: string): Promise<Admin> {
    try {
      return await this.exchange(callbackUrl);
    } catch (e) {
      if (e instanceof NeedsSignIn) this.failure = e;
      throw e;
    }
  }

  private async exchange(callbackUrl: string): Promise<Admin> {
    const params = new URL(callbackUrl).searchParams;
    const pending = readPending(this.store.read(pendingKey));
    this.store.delete(pendingKey);

    // The state must be the one this tab sent, so nobody can slip their own code into our session.
    if (!pending || params.get('state') !== pending.state) throw new NeedsSignIn('STATE_MISMATCH');
    const code = params.get('code');
    if (!code) throw new NeedsSignIn(params.get('error') ?? 'NO_CODE');

    try {
      return this.accept(await this.api.exchangeCode(code, pending.verifier, this.redirectUri));
    } catch (e) {
      if (e instanceof ApiError && e.status < 500) throw new NeedsSignIn(e.code ?? 'CODE_REFUSED');
      throw e;
    }
  }

  /** Runs an admin call, refreshing once on a 401; throws NeedsSignIn if that fails too. */
  async authorized<T>(call: (accessToken: string) => Promise<T>): Promise<T> {
    try {
      return await call(await this.validAccessToken());
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401) throw e;
      return await call(await this.refresh());
    }
  }

  /** Rotates both tokens, sharing one request between concurrent callers. */
  refresh(): Promise<string> {
    this.refreshing ??= this.doRefresh().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  async signOut(): Promise<void> {
    const refreshToken = this.store.read(refreshKey);
    if (refreshToken !== null) {
      try {
        await this.api.logout(refreshToken);
      } catch {
        // The local session ends anyway; the server-side one expires on its own.
      }
    }
    this.clear();
  }

  private async validAccessToken(): Promise<string> {
    if (this.accessToken !== null && this.expiresAt > this.now() + refreshMarginMs) return this.accessToken;
    return this.refresh();
  }

  private async doRefresh(): Promise<string> {
    const refreshToken = this.store.read(refreshKey);
    if (refreshToken === null) {
      this.clear();
      throw new NeedsSignIn('NOT_SIGNED_IN');
    }
    try {
      const tokens = await this.api.refresh(refreshToken);
      this.accept(tokens);
      return tokens.accessToken;
    } catch (e) {
      // invalid_grant: expired, signed out elsewhere, deactivated, or already rotated. Only a new sign-in helps.
      if (e instanceof ApiError && (e.code === 'invalid_grant' || e.status === 401)) {
        this.ended = this.signedIn;
        this.clear();
        throw new NeedsSignIn(e.code ?? 'INVALID_REFRESH_TOKEN');
      }
      throw e;
    }
  }

  private clear(): void {
    const wasSignedIn = this.signedIn;
    this.accessToken = null;
    this.expiresAt = 0;
    this.current = null;
    this.store.delete(refreshKey);
    this.store.delete(usernameKey);
    if (wasSignedIn) this.notify();
  }

  /** Saves the new refresh token straight away, because the old one has stopped working. */
  private accept(tokens: TokenPair): Admin {
    this.store.write(refreshKey, tokens.refreshToken);
    this.accessToken = tokens.accessToken;
    this.expiresAt = this.now() + tokens.expiresIn * 1000;
    const claims = readClaims(tokens.accessToken);
    const username = tokens.idToken ? str(readClaims(tokens.idToken).preferred_username) : '';
    if (username) this.store.write(usernameKey, username);
    const wasSignedIn = this.signedIn;
    this.current = {
      id: str(claims.sub),
      username: username || (this.store.read(usernameKey) ?? ''),
      role: str(claims.role),
      facilityId: str(claims.facility_id),
    };
    if (!wasSignedIn) this.notify();
    return this.current;
  }

  private notify(): void {
    for (const listener of this.listeners) listener();
  }
}

const str = (value: unknown): string => (typeof value === 'string' ? value : '');

function readPending(raw: string | null): { verifier: string; state: string } | null {
  try {
    const value: unknown = raw ? JSON.parse(raw) : null;
    if (typeof value !== 'object' || value === null) return null;
    const { verifier, state } = value as Record<string, unknown>;
    return typeof verifier === 'string' && typeof state === 'string' ? { verifier, state } : null;
  } catch {
    return null;
  }
}

const base64Url = (bytes: Uint8Array): string =>
  btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

function randomUrlSafe(bytes: number): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(bytes)));
}

/** The PKCE S256 challenge for a verifier (RFC 7636 §4.2). */
export async function sha256UrlSafe(text: string): Promise<string> {
  return base64Url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text))));
}

/** Reads the token's contents for display only; the backend is the one that checks it's genuine. */
function readClaims(jwt: string): Record<string, unknown> {
  const parts = jwt.split('.');
  if (parts.length !== 3 || !parts[1]) return {};
  try {
    const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const bytes = Uint8Array.from(atob(b64.padEnd(Math.ceil(b64.length / 4) * 4, '=')), (c) => c.charCodeAt(0));
    const claims: unknown = JSON.parse(new TextDecoder().decode(bytes));
    return typeof claims === 'object' && claims !== null ? (claims as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}
