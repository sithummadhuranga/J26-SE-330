// Helpers for the end-to-end tests: everything here talks to the real Docker stack through the API gateway.
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createHmac, randomBytes } from 'node:crypto';
import { MemoryRouter } from 'react-router';
import { App } from '../app/App';
import { AdminApi } from '../core/api';
import { AuthSession } from '../core/session';
import { MemoryTokenStore } from '../core/tokenStore';

export const backendUrl = process.env.DASHBOARD_BACKEND_URL ?? 'http://localhost:8080';

/** The local demo admin that `docker compose up` seeds (seed-demo-users). */
export const demoAdmin = { username: 'admin.demo', password: 'Demo-Admin-2026!', facilityId: 'fac-001' };

export const refreshKey = 'cdss_admin_refresh_v1';

/** Fails fast with a clear message when the stack is not running. */
export async function requireBackend(): Promise<void> {
  const health = await fetch(`${backendUrl}/health`).catch(() => null);
  if (!health?.ok) throw new Error(`No backend at ${backendUrl}: start it with \`docker compose up -d\`.`);
}

/** A username no earlier run has used, e.g. e2e.reg.mv1x2k4f9q. */
export const uniqueName = (tag: string) => `e2e.${tag}.${Date.now().toString(36)}${randomBytes(2).toString('hex')}`;

export const strongPassword = 'E2e-Test-Pass-2026!';

/** A dashboard session on the real backend; [now] lets a test move the clock forward. */
export function newSession(store = new MemoryTokenStore(), now?: () => number, baseUrl = backendUrl): AuthSession {
  return new AuthSession(new AdminApi(baseUrl), store, now);
}

/** The demo admin, signed in through the real identity service. */
export async function adminSession(): Promise<AuthSession> {
  const session = newSession();
  await session.signIn(demoAdmin.username, demoAdmin.password);
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

/** The mobile app's login (with a device id), which also registers the device on first use. */
export async function mobileLogin(username: string, password: string, deviceId: string) {
  const response = await fetch(`${backendUrl}/v1/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password, deviceId }),
  });
  const body = (await response.json().catch(() => ({}))) as { code?: string; accessToken?: string };
  return { status: response.status, code: body.code ?? null, accessToken: body.accessToken ?? null };
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

export async function signInOnScreen(user: ReturnType<typeof userEvent.setup>, username: string, password: string) {
  await user.type(screen.getByLabelText('Username'), username);
  await user.type(screen.getByLabelText('Password'), password);
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

/** Waits for a toast containing [text]; real network calls can take a moment. */
export const findToast = (text: string | RegExp) => screen.findByText(text, {}, { timeout: 15_000 });

export const slow = { timeout: 15_000 };
