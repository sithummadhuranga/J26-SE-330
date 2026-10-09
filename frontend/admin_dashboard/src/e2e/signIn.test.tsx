import { screen, waitFor } from '@testing-library/react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { ApiError, TransportError } from '../core/api';
import { NeedsSignIn } from '../core/session';
import { MemoryTokenStore } from '../core/tokenStore';
import {
  adminSession,
  demoAdmin,
  findToast,
  mfaCall,
  newSession,
  refreshKey,
  registerClinician,
  renderDashboard,
  requireBackend,
  signInOnScreen,
  slow,
  strongPassword,
  totpCode,
  uniqueName,
  cleanUp,
} from './backend';

beforeAll(requireBackend);
afterAll(cleanUp);

describe('signing in (real identity service)', () => {
  it('the demo admin signs in and sees their name and facility', async () => {
    const user = renderDashboard(newSession());
    await signInOnScreen(user, demoAdmin.username, demoAdmin.password);

    expect(await screen.findByRole('heading', { name: 'Clinicians' }, slow)).toBeInTheDocument();
    expect(screen.getByTestId('signed-in-as')).toHaveTextContent(demoAdmin.username);
    expect(screen.getByText(`Facility ${demoAdmin.facilityId}`)).toBeInTheDocument();
  });

  it('a wrong password is refused with a plain message and stays on the sign-in screen', async () => {
    const user = renderDashboard(newSession());
    await signInOnScreen(user, demoAdmin.username, 'not-the-password');

    expect(await findToast('The username or password is incorrect.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('only admins can use the dashboard', async () => {
    const admin = await adminSession();
    const nurse = uniqueName('nurse');
    await registerClinician(admin, nurse);
    const user = renderDashboard(newSession());

    await signInOnScreen(user, nurse, strongPassword);

    expect(await findToast(/Only admins can sign in to the dashboard/)).toBeInTheDocument();
  });

  it('five wrong passwords lock the account; an admin unlocks it and it can sign in again', async () => {
    const admin = await adminSession();
    const name = uniqueName('lock');
    await registerClinician(admin, name, 'admin');
    for (let i = 0; i < 5; i++) await newSession().signIn(name, 'wrong-password-1').catch(() => undefined);

    await expect(newSession().signIn(name, strongPassword)).rejects.toMatchObject({ code: 'CREDENTIAL_LOCKED' });
    const locked = (await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name);
    expect(locked?.locked).toBe(true);

    await admin.authorized((t) => admin.api.unlock(t, name));
    await expect(newSession().signIn(name, strongPassword)).resolves.toMatchObject({ username: name });
  });

  it('with MFA on, the code field appears and a valid code opens the dashboard; an admin reset turns it off', async () => {
    const admin = await adminSession();
    const name = uniqueName('mfa');
    await registerClinician(admin, name, 'admin');

    // Turn MFA on for that account the way the account owner would.
    const owner = newSession();
    await owner.signIn(name, strongPassword);
    const { secret } = await owner.authorized((t) => mfaCall<{ secret: string }>('enroll', t));
    const step = Math.floor(Date.now() / 30_000);
    await owner.authorized((t) => mfaCall('confirm', t, { code: totpCode(secret, step) }));

    const user = renderDashboard(newSession());
    await signInOnScreen(user, name, strongPassword);
    const codeField = await screen.findByLabelText('Authenticator code', {}, slow);
    await user.type(codeField, totpCode(secret, step + 1)); // the confirm code cannot be replayed
    await user.click(screen.getByRole('button', { name: 'Verify and sign in' }));
    expect(await screen.findByRole('heading', { name: 'Clinicians' }, slow)).toBeInTheDocument();

    await admin.authorized((t) => admin.api.resetMfa(t, name));
    await expect(newSession().signIn(name, strongPassword)).resolves.toMatchObject({ username: name });
  });

  it('a session the server ended sends the admin back to sign-in with an explanation', async () => {
    let now = Date.now();
    const store = new MemoryTokenStore();
    const user = renderDashboard(newSession(store, () => now));
    await signInOnScreen(user, demoAdmin.username, demoAdmin.password);
    await screen.findByRole('heading', { name: 'Clinicians' }, slow);

    // Signed out elsewhere, then the access token runs out: the next refresh is refused.
    await newSession().api.logout(store.read(refreshKey)!);
    now += 16 * 60_000;
    await user.click(screen.getByRole('link', { name: 'Devices' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument(), slow);
    expect(await findToast('Your session has ended. Please sign in again.')).toBeInTheDocument();
  });

  it('when the server cannot be reached the admin is told so in words', async () => {
    const user = renderDashboard(newSession(new MemoryTokenStore(), undefined, 'http://127.0.0.1:9'));
    await signInOnScreen(user, demoAdmin.username, demoAdmin.password);

    expect(await findToast('Cannot reach the server. Check your connection and try again.')).toBeInTheDocument();
  });

  it('refresh rotates the token, a reload restores the session, and sign-out ends it on the server', async () => {
    const store = new MemoryTokenStore();
    const session = newSession(store);
    await session.signIn(demoAdmin.username, demoAdmin.password);

    const first = store.read(refreshKey)!;
    await session.refresh();
    expect(store.read(refreshKey)).not.toBe(first);
    await expect(newSession().api.refresh(first)).rejects.toBeInstanceOf(ApiError); // the old one is spent

    const reloaded = newSession(store);
    expect(await reloaded.restore()).toBe(true);
    const last = store.read(refreshKey)!;
    await reloaded.signOut();
    await expect(newSession().api.refresh(last)).rejects.toMatchObject({ status: 401 });
    await expect(reloaded.authorized((t) => reloaded.api.clinicians(t))).rejects.toBeInstanceOf(NeedsSignIn);
  });

  it('admin calls without a valid token are refused', async () => {
    await expect(newSession().api.clinicians('not-a-token')).rejects.toMatchObject({ status: 401 });
    await expect(newSession(new MemoryTokenStore(), undefined, 'http://127.0.0.1:9').api.devices('x')).rejects.toBeInstanceOf(
      TransportError,
    );
  });
});
