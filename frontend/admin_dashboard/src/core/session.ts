import { AdminApi, ApiError } from './api';
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

/** The signed-in admin, read from the access token plus the username typed at login. */
export interface Admin {
  id: string;
  username: string;
  role: string;
  facilityId: string;
}

const refreshKey = 'cdss_admin_refresh_v1';
const usernameKey = 'cdss_admin_username_v1';

/** Refresh a little before expiry so a request never leaves with a token about to lapse. */
export const refreshMarginMs = 60_000;

/** Keeps the admin signed in: access token in memory, refresh token in the tab's session storage. */
export class AuthSession {
  readonly api: AdminApi;
  private readonly store: TokenStore;
  private readonly now: () => number;
  private readonly listeners = new Set<() => void>();

  private accessToken: string | null = null;
  private expiresAt = 0;
  private current: Admin | null = null;
  private refreshing: Promise<string> | null = null;
  private ended = false;

  constructor(api: AdminApi, store: TokenStore, now: () => number = Date.now) {
    this.api = api;
    this.store = store;
    this.now = now;
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

  /** Throws ApiError with the backend's reason code (401 MFA_REQUIRED: ask for a code). */
  async signIn(username: string, password: string, totp?: string): Promise<Admin> {
    const tokens = await this.api.login(username, password, totp);
    this.store.write(usernameKey, username);
    return this.accept(tokens);
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
      if (e instanceof ApiError && e.status === 401) {
        // Expired, logged out elsewhere, deactivated, or already rotated: only a new sign-in helps.
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
    if (wasSignedIn) this.notify();
  }

  /** Saves the new refresh token straight away, because the old one has stopped working. */
  private accept(tokens: TokenPair): Admin {
    this.store.write(refreshKey, tokens.refreshToken);
    this.accessToken = tokens.accessToken;
    this.expiresAt = this.now() + tokens.expiresIn * 1000;
    const claims = readClaims(tokens.accessToken);
    const wasSignedIn = this.signedIn;
    this.current = {
      id: str(claims.sub),
      username: this.store.read(usernameKey) ?? '',
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
