import { screen, waitFor } from '@testing-library/react';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { TransportError } from '../core/api';
import { browser } from '../core/navigation';
import { NeedsSignIn, sha256UrlSafe } from '../core/session';
import { MemoryTokenStore } from '../core/tokenStore';
import {
  LoginPageError,
  adminSession,
  backendUrl,
  cleanUp,
  demoAdmin,
  findToast,
  mfaCall,
  newSession,
  ownPassword,
  redirectUri,
  refreshKey,
  registerClinician,
  renderDashboard,
  requireBackend,
  signInOnScreen,
  signInThroughLoginPage,
  slow,
  strongPassword,
  throughLoginPage,
  totpCode,
  uniqueName,
} from './backend';

beforeAll(requireBackend);
afterAll(cleanUp);

describe('signing in through the identity service (OpenID Connect, ADR 0006)', () => {
  it('the demo admin signs in and sees their name and facility', async () => {
    const session = newSession();
    const user = renderDashboard(session);
    await signInOnScreen(user, session, demoAdmin.username, demoAdmin.password);

    expect(await screen.findByRole('heading', { name: 'Clinicians' }, slow)).toBeInTheDocument();
    expect(screen.getByTestId('signed-in-as')).toHaveTextContent(demoAdmin.username);
    expect(screen.getByText(`Facility ${demoAdmin.facilityId}`)).toBeInTheDocument();
  });

  it('"Sign in" opens the login page with PKCE S256 and a state only this tab knows; no password field here', async () => {
    const store = new MemoryTokenStore();
    const user = renderDashboard(newSession(store));
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument();
    const go = vi.spyOn(browser, 'go').mockImplementation(() => undefined);

    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(go).toHaveBeenCalled());

    const url = new URL(go.mock.calls[0]![0]);
    go.mockRestore();
    expect(`${url.origin}${url.pathname}`).toBe(`${backendUrl}/connect/authorize`);
    expect(Object.fromEntries(url.searchParams)).toMatchObject({
      response_type: 'code',
      client_id: 'admin-dashboard',
      redirect_uri: redirectUri,
      code_challenge_method: 'S256',
    });
    const pending = JSON.parse(store.read('cdss_admin_pending_v1')!) as { verifier: string; state: string };
    expect(url.searchParams.get('state')).toBe(pending.state);
    expect(url.searchParams.get('code_challenge')).toBe(await sha256UrlSafe(pending.verifier));
  });

  it('a wrong password is refused on the login page, which never sends the browser back', async () => {
    const session = newSession();
    await expect(signInThroughLoginPage(session, demoAdmin.username, 'not-the-password')).rejects.toMatchObject({
      code: 'INVALID_CREDENTIALS',
    });
    expect(session.signedIn).toBe(false);
  });

  it('only admins can use the dashboard', async () => {
    const admin = await adminSession();
    const nurse = uniqueName('nurse');
    await registerClinician(admin, nurse);

    await expect(signInThroughLoginPage(newSession(), nurse, strongPassword)).rejects.toMatchObject({
      code: 'CLIENT_NOT_ALLOWED',
    });
  });

  it('five wrong passwords lock the account; an admin unlocks it and it can sign in again', async () => {
    const admin = await adminSession();
    const name = uniqueName('lock');
    await registerClinician(admin, name, 'admin');
    for (let i = 0; i < 5; i++) await signInThroughLoginPage(newSession(), name, 'wrong-password-1').catch(() => undefined);

    await expect(signInThroughLoginPage(newSession(), name, strongPassword)).rejects.toMatchObject({
      code: 'CREDENTIAL_LOCKED',
    });
    const locked = (await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name);
    expect(locked?.locked).toBe(true);

    await admin.authorized((t) => admin.api.unlock(t, name));
    // Unlocked; the admin chose this password, so the first sign-in also replaces it.
    await expect(signInThroughLoginPage(newSession(), name, strongPassword, undefined, ownPassword)).resolves.toMatchObject({
      username: name,
    });
  });

  it('with MFA on, the login page asks for the code and a valid one opens the dashboard; an admin reset turns it off', async () => {
    const admin = await adminSession();
    const name = uniqueName('mfa');
    await registerClinician(admin, name, 'admin');

    // Turn MFA on for that account the way the account owner would.
    const owner = newSession();
    await signInThroughLoginPage(owner, name, strongPassword, undefined, ownPassword);
    const { secret } = await owner.authorized((t) => mfaCall<{ secret: string }>('enroll', t));
    const step = Math.floor(Date.now() / 30_000);
    await owner.authorized((t) => mfaCall('confirm', t, { code: totpCode(secret, step) }));

    await expect(signInThroughLoginPage(newSession(), name, ownPassword, '000000')).rejects.toMatchObject({
      code: 'INVALID_TOTP',
    });
    const session = newSession();
    const user = renderDashboard(session);
    await signInOnScreen(user, session, name, ownPassword, totpCode(secret, step + 1)); // the confirm code cannot be replayed
    expect(await screen.findByRole('heading', { name: 'Clinicians' }, slow)).toBeInTheDocument();

    await admin.authorized((t) => admin.api.resetMfa(t, name));
    await expect(signInThroughLoginPage(newSession(), name, ownPassword)).resolves.toMatchObject({ username: name });
  });

  it('a callback this tab did not start is refused, and the sign-in screen says so', async () => {
    const other = newSession();
    const callback = await throughLoginPage(await other.beginSignIn(), demoAdmin.username, demoAdmin.password);

    const session = newSession();
    await session.beginSignIn(); // this tab's own sign-in, with a different state
    await expect(session.completeSignIn(callback)).rejects.toMatchObject({ reason: 'STATE_MISMATCH' });

    renderDashboard(session);
    expect(await findToast('That sign-in did not start in this tab. Please sign in again.')).toBeInTheDocument();
  });

  it('a callback cannot be used twice', async () => {
    const session = newSession();
    const callback = await throughLoginPage(await session.beginSignIn(), demoAdmin.username, demoAdmin.password);
    await session.completeSignIn(callback);

    await expect(session.completeSignIn(callback)).rejects.toBeInstanceOf(NeedsSignIn);
  });

  it('a session the server ended sends the admin back to sign-in with an explanation', async () => {
    let now = Date.now();
    const store = new MemoryTokenStore();
    const session = newSession(store, () => now);
    const user = renderDashboard(session);
    await signInOnScreen(user, session, demoAdmin.username, demoAdmin.password);
    await screen.findByRole('heading', { name: 'Clinicians' }, slow);

    // Signed out elsewhere, then the access token runs out: the next refresh is refused.
    await newSession().api.logout(store.read(refreshKey)!);
    now += 16 * 60_000;
    await user.click(screen.getByRole('link', { name: 'Devices' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument(), slow);
    expect(await findToast('Your session has ended. Please sign in again.')).toBeInTheDocument();
  });

  it('refresh rotates the token, a reload restores the session, and sign-out ends it on the server', async () => {
    const store = new MemoryTokenStore();
    const session = newSession(store);
    await signInThroughLoginPage(session, demoAdmin.username, demoAdmin.password);

    const first = store.read(refreshKey)!;
    await session.refresh();
    expect(store.read(refreshKey)).not.toBe(first);
    await expect(newSession().api.refresh(first)).rejects.toMatchObject({ code: 'invalid_grant' }); // the old one is spent

    const reloaded = newSession(store);
    expect(await reloaded.restore()).toBe(true);
    expect(reloaded.admin?.username).toBe(demoAdmin.username);
    const last = store.read(refreshKey)!;
    await reloaded.signOut();
    await expect(newSession().api.refresh(last)).rejects.toMatchObject({ code: 'invalid_grant' });
    await expect(reloaded.authorized((t) => reloaded.api.clinicians(t))).rejects.toBeInstanceOf(NeedsSignIn);
  });

  it('admin calls without a valid token are refused, and an unreachable server is reported as such', async () => {
    await expect(newSession().api.clinicians('not-a-token')).rejects.toMatchObject({ status: 401 });
    await expect(newSession(new MemoryTokenStore(), undefined, 'http://127.0.0.1:9').api.devices('x')).rejects.toBeInstanceOf(
      TransportError,
    );
    await expect(throughLoginPage(`${backendUrl}/connect/authorize?client_id=admin-dashboard`, 'a', 'b')).rejects.toBeInstanceOf(
      LoginPageError,
    );
  });
});
