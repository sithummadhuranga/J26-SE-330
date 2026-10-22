// Helpers for the end-to-end tests. Everything here talks to the real Docker stack through the API gateway.
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createHmac, randomBytes } from 'node:crypto';
import { MemoryRouter } from 'react-router';
import { expect, vi } from 'vitest';
import { App } from '../app/App';
import { AdminApi } from '../core/api';
import { browser } from '../core/navigation';
import { AuthSession, type Admin } from '../core/session';
import { MemoryTokenStore } from '../core/tokenStore';

export const backendUrl = process.env.DASHBOARD_BACKEND_URL ?? 'http://localhost:8080';

/** The dashboard's registered redirect URI (the identity service's Oidc:DashboardRedirectUris). */
export const redirectUri = process.env.DASHBOARD_REDIRECT_URI ?? 'http://localhost:3001/callback';

/** The local demo admin that `docker compose up` seeds (seed-demo-users). */
export const demoAdmin = { username: 'admin.demo', password: 'Demo-Admin-2026!', facilityId: 'fac-001' };

export const refreshKey = 'cdss_admin_refresh_v1';

/** Fails fast with a clear message when the stack is not running. */
export async function requireBackend(): Promise<void> {
  const health = await fetch(`${backendUrl}/health`).catch(() => null);
  if (!health?.ok) throw new Error(`No backend at ${backendUrl}: start it with \`docker compose up -d\`.`);
}

/** A username no earlier run has used, like e2e.reg.mv1x2k4f9q. */
export const uniqueName = (tag: string) => `e2e.${tag}.${Date.now().toString(36)}${randomBytes(2).toString('hex')}`;

export const strongPassword = 'E2e-Test-Pass-2026!';

/** What a clinician picks on the login page to replace [strongPassword], which their admin chose. */
export const ownPassword = 'Own-E2e-Pass-2026!';

/** A dashboard session on the real backend. A test can use [now] to move the clock forward. */
export function newSession(store = new MemoryTokenStore(), now?: () => number, baseUrl = backendUrl): AuthSession {
  return new AuthSession(new AdminApi(baseUrl), store, now, redirectUri);
}

/** The login page refused the sign-in. [code] is the reason it shows (data-code), such as CLIENT_NOT_ALLOWED. */
export class LoginPageError extends Error {
  readonly code: string | null;

  constructor(code: string | null) {
    super(`login page refused: ${code}`);
    this.name = 'LoginPageError';
    this.code = code;
  }
}

const unescapeHtml = (html: string) =>
  html
    .replace(/&#x([0-9a-f]+);/gi, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
    .replace(/&quot;/g, '"')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');

/**
 * Does what a browser does between the dashboard and /callback. It opens [authorizeUrl], fills in the identity
 * service's real login page (and the authenticator code if asked), and follows redirects with cookies. It returns
 * the URL the identity service sent the browser back to, or throws LoginPageError if the page said no.
 * If an admin set the password, the page asks for a new one. [newPassword] is used for that, and without it
 * this throws PASSWORD_CHANGE_REQUIRED.
 */
export async function throughLoginPage(
  authorizeUrl: string,
  username: string,
  password: string,
  totp?: string,
  newPassword?: string,
) {
  const cookies = new Map<string, string>();
  let url = authorizeUrl;

  const request = async (target: string, form?: Record<string, string>) => {
    const response = await fetch(target, {
      method: form ? 'POST' : 'GET',
      redirect: 'manual',
      headers: {
        cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; '),
        ...(form ? { 'Content-Type': 'application/x-www-form-urlencoded' } : {}),
      },
      body: form ? new URLSearchParams(form).toString() : undefined,
    });
    for (const header of response.headers.getSetCookie()) {
      const pair = header.split(';')[0]!;
      const eq = pair.indexOf('=');
      cookies.set(pair.slice(0, eq), pair.slice(eq + 1));
    }
    return response;
  };

  /** Follows redirects inside the backend. It stops at a page, or reports where the browser was sent off to. */
  const follow = async (target: string, form?: Record<string, string>) => {
    url = target;
    let response = await request(target, form);
    while (response.status >= 300 && response.status < 400) {
      const next = new URL(response.headers.get('location')!, url).toString();
      if (!next.startsWith(backendUrl)) return { leftFor: next, page: '' };
      url = next;
      response = await request(next);
    }
    return { leftFor: null, page: await response.text() };
  };

  const fields = (page: string): Record<string, string> =>
    Object.fromEntries(
      [...page.matchAll(/<input[^>]*?name="([^"]+)"[^>]*?value="([^"]*)"/g)].map((m) => [m[1]!, unescapeHtml(m[2]!)]),
    );
  const errorCode = (page: string) => /data-code="([^"]+)"/.exec(page)?.[1] ?? null;

  let step = await follow(authorizeUrl);
  if (step.leftFor) return step.leftFor;
  step = await follow(url, { ...fields(step.page), Username: username, Password: password });
  if (!step.leftFor && totp !== undefined && /name="Totp"/.test(step.page) && !errorCode(step.page)) {
    step = await follow(url, { ...fields(step.page), Totp: totp });
  }
  if (!step.leftFor && /name="NewPassword"/.test(step.page) && !errorCode(step.page)) {
    if (newPassword === undefined) throw new LoginPageError('PASSWORD_CHANGE_REQUIRED');
    step = await follow(url, { ...fields(step.page), NewPassword: newPassword, ConfirmPassword: newPassword });
  }
  if (step.leftFor) return step.leftFor;
  throw new LoginPageError(errorCode(step.page));
}

/** Signs [session] in through the real login page, as clicking "Sign in" and coming back to /callback would. */
export async function signInThroughLoginPage(
  session: AuthSession,
  username: string,
  password: string,
  totp?: string,
  newPassword?: string,
): Promise<Admin> {
  return session.completeSignIn(await throughLoginPage(await session.beginSignIn(), username, password, totp, newPassword));
}

/** The demo admin, signed in through the real identity service. */
export async function adminSession(): Promise<AuthSession> {
  const session = newSession();
  await signInThroughLoginPage(session, demoAdmin.username, demoAdmin.password);
  return session;
}

/** Accounts this test file created, deactivated by [cleanUp] so test runs never leave active accounts behind. */
const created = new Set<string>();
export const track = (username: string) => void created.add(username);

/** Registers a clinician in the demo facility through the real admin API. */
export async function registerClinician(admin: AuthSession, username: string, role: 'nurse' | 'admin' = 'nurse') {
  track(username);
  await admin.authorized((t) =>
    admin.api.registerClinician(t, { username, password: strongPassword, fullName: `E2E ${username}`, role }),
  );
}

/** Deactivates every account this file created that is still active (use in afterAll). */
export async function cleanUp(): Promise<void> {
  if (created.size === 0) return;
  const admin = await adminSession();
  const active = (await admin.authorized((t) => admin.api.clinicians(t))).filter((c) => created.has(c.username) && c.active);
  for (const c of active) await admin.authorized((t) => admin.api.deactivate(t, c.username));
  created.clear();
}

/**
 * A phone signing in with a device id, which also registers the device the first time. It uses the password grant
 * that the local stack turns on for tests (Oidc:AllowPasswordGrant). The app itself uses the login page.
 */
export async function mobileLogin(username: string, password: string, deviceId: string) {
  const response = await fetch(`${backendUrl}/connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'password', client_id: 'mobile', username, password, device_id: deviceId }),
  });
  const body = (await response.json().catch(() => ({}))) as { error_description?: string; access_token?: string };
  return { status: response.status, code: body.error_description ?? null, accessToken: body.access_token ?? null };
}

/** Posts to an MFA endpoint as the signed-in clinician. */
export async function mfaCall<T>(path: 'enroll' | 'confirm', accessToken: string, body?: unknown): Promise<T> {
  const response = await fetch(`${backendUrl}/v1/auth/mfa/${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${accessToken}` },
    body: JSON.stringify(body ?? {}),
  });
  if (!response.ok) throw new Error(`mfa/${path} answered ${response.status}`);
  return (await response.json().catch(() => ({}))) as T;
}

/** The 6-digit code an authenticator app would show for [secret] at a given 30-second [step]. */
export function totpCode(secret: string, step = Math.floor(Date.now() / 30_000)): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const ch of secret.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g)!.map((b) => parseInt(b, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hmac = createHmac('sha1', key).update(counter).digest();
  const offset = hmac[hmac.length - 1]! & 0x0f;
  return String((hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

/** Renders the whole dashboard against the real backend. */
export function renderDashboard(session: AuthSession, path = '/') {
  const user = userEvent.setup();
  render(
    <MemoryRouter initialEntries={[path]}>
      <App session={session} />
    </MemoryRouter>,
  );
  return user;
}

/**
 * Clicks "Sign in" on the dashboard, signs in on the real login page, and comes back to /callback the way the
 * browser would (main.tsx finishes a sign-in the same way).
 */
export async function signInOnScreen(
  user: ReturnType<typeof userEvent.setup>,
  session: AuthSession,
  username: string,
  password: string,
  totp?: string,
  newPassword?: string,
) {
  const go = vi.spyOn(browser, 'go').mockImplementation(() => undefined);
  try {
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(go).toHaveBeenCalled());
    await session.completeSignIn(await throughLoginPage(go.mock.calls[0]![0], username, password, totp, newPassword));
  } finally {
    go.mockRestore();
  }
}

/** Waits for a toast containing [text], since real network calls can take a moment. */
export const findToast = (text: string | RegExp) => screen.findByText(text, {}, { timeout: 15_000 });

export const slow = { timeout: 15_000 };
