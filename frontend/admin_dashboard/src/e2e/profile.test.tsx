import { screen, waitFor, within } from '@testing-library/react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  adminSession,
  cleanUp,
  demoAdmin,
  findToast,
  LoginPageError,
  mobileLogin,
  newSession,
  ownPassword,
  registerClinician,
  renderDashboard,
  requireBackend,
  signInOnScreen,
  signInThroughLoginPage,
  slow,
  strongPassword,
  uniqueName,
} from './backend';

beforeAll(requireBackend);
afterAll(cleanUp);

async function signedIn() {
  const session = newSession();
  const user = renderDashboard(session);
  await signInOnScreen(user, session, demoAdmin.username, demoAdmin.password);
  await screen.findByRole('heading', { name: 'Clinicians' }, slow);
  return user;
}

/** A new admin, so a rename never changes the demo admin's name that other tests look for. */
async function signedInAsNewAdmin(path = '/') {
  const name = uniqueName('prof');
  await registerClinician(await adminSession(), name, 'admin');
  const session = newSession();
  const user = renderDashboard(session, path);
  await signInOnScreen(user, session, name, strongPassword, undefined, ownPassword);
  await screen.findByRole('button', { name: 'Your profile' }, slow);
  return { user, name };
}

/**
 * The clinicians-table row with [text], paging with the arrows: other test files register clinicians at the same
 * time, so this one may not be on page 1. Looks inside the table only (the header shows your username too).
 */
async function rowInTable(user: Awaited<ReturnType<typeof signedInAsNewAdmin>>['user'], text: string) {
  for (;;) {
    const cell = within(await screen.findByRole('table', {}, slow)).queryByText(text);
    if (cell) return cell.closest('tr')!;
    const next = screen.getByRole('button', { name: 'Next page' });
    if ((next as HTMLButtonElement).disabled) throw new Error(`"${text}" is on no page`);
    await user.click(next);
  }
}

describe('profile panel from the avatar (real identity service)', () => {
  it('shows who you are from /v1/me, and closes on Escape and on a click outside', async () => {
    const user = await signedIn();
    const avatar = screen.getByRole('button', { name: 'Your profile' });
    expect(avatar).toHaveAttribute('aria-expanded', 'false');

    await user.click(avatar);
    const panel = await screen.findByRole('dialog', { name: 'Your profile' });
    expect(await within(panel).findByText('Demo Admin', {}, slow)).toBeInTheDocument();
    expect(within(panel).getByText(demoAdmin.username)).toBeInTheDocument();
    expect(within(panel).getByText(`Admin · Facility ${demoAdmin.facilityId}`)).toBeInTheDocument();
    expect(within(panel).getByText('Two-step sign-in', { selector: 'dt' })).toBeInTheDocument();
    // This test just signed in, so the last sign-in is a real time, not a dash.
    const lastSignIn = within(panel).getByText('Last sign-in').nextElementSibling!;
    expect(lastSignIn.textContent).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
    expect(avatar).toHaveAttribute('aria-expanded', 'true');

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Your profile' })).not.toBeInTheDocument();
    expect(avatar).toHaveFocus();

    await user.click(avatar);
    await screen.findByRole('dialog', { name: 'Your profile' });
    await user.click(screen.getByRole('heading', { name: 'Clinicians' }));
    expect(screen.queryByRole('dialog', { name: 'Your profile' })).not.toBeInTheDocument();
  });

  it('Edit profile from the panel renames you, and the panel and the clinicians list show it at once', async () => {
    const { user, name } = await signedInAsNewAdmin();
    await screen.findByRole('heading', { name: 'Clinicians' }, slow);
    await user.click(screen.getByRole('button', { name: 'Your profile' }));
    const panel = await screen.findByRole('dialog', { name: 'Your profile' });
    await user.click(await within(panel).findByRole('button', { name: 'Edit profile' }, slow));

    const dialog = screen.getByRole('dialog', { name: 'Edit profile' });
    const field = within(dialog).getByLabelText('Full name');
    expect(field).toHaveValue(`E2E ${name}`);
    await user.clear(field);
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(within(dialog).getByText('Enter your full name')).toBeInTheDocument();

    await user.type(field, '  Renamed In Test  ');
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await findToast('Your name is now Renamed In Test.')).toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: 'Edit profile' })).not.toBeInTheDocument();

    // The clinicians list (another admin's view of you) refreshes without a reload.
    const row = await rowInTable(user, `${name} (you)`); // your own row is marked "(you)"
    await waitFor(() => expect(within(row).getByText('Renamed In Test')).toBeInTheDocument(), slow);
    await user.click(screen.getByRole('button', { name: 'Your profile' }));
    expect(within(await screen.findByRole('dialog', { name: 'Your profile' })).getByText('Renamed In Test')).toBeInTheDocument();

    // Saved on the server: a fresh read of the profile has it.
    const admin = await adminSession();
    expect((await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name)?.fullName).toBe(
      'Renamed In Test',
    );
  });

  it('My account shows your full name and has its own Edit profile button', async () => {
    const { user, name } = await signedInAsNewAdmin('/account');
    expect(await screen.findByText(`E2E ${name}`, {}, slow)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Edit profile' }));
    const field = within(screen.getByRole('dialog', { name: 'Edit profile' })).getByLabelText('Full name');
    await user.clear(field);
    await user.type(field, 'From My Account');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('From My Account', { selector: '.profile__name' }, slow)).toBeInTheDocument();
  });

  it('Change password checks the form, refuses a wrong current password, then signs you out for the new one', async () => {
    const { user, name } = await signedInAsNewAdmin();
    const newPassword = 'Changed-E2e-Pass-2026!';
    await user.click(screen.getByRole('button', { name: 'Your profile' }));
    const panel = await screen.findByRole('dialog', { name: 'Your profile' });
    await user.click(within(panel).getByRole('button', { name: 'Change password' }));
    const dialog = screen.getByRole('dialog', { name: 'Change password' });
    const submit = () => user.click(within(dialog).getByRole('button', { name: 'Change password' }));

    await submit();
    expect(within(dialog).getByText('Enter your current password')).toBeInTheDocument();
    expect(within(dialog).getByText('At least 12 characters')).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText('Current password'), 'definitely-wrong-1');
    await user.type(within(dialog).getByLabelText('New password'), newPassword);
    await user.type(within(dialog).getByLabelText('New password again'), newPassword + 'x');
    await submit();
    expect(within(dialog).getByText('The two new passwords do not match')).toBeInTheDocument();

    await user.clear(within(dialog).getByLabelText('New password again'));
    await user.type(within(dialog).getByLabelText('New password again'), newPassword);
    await submit();
    expect(await within(dialog).findByText('Your current password is not right.', {}, slow)).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Current password')).toHaveValue('');

    await user.type(within(dialog).getByLabelText('Current password'), ownPassword);
    await submit();
    expect(await screen.findByRole('button', { name: 'Sign in' }, slow)).toBeInTheDocument();
    expect(await findToast('Sign in with your new password.')).toBeInTheDocument();

    await expect(signInThroughLoginPage(newSession(), name, ownPassword)).rejects.toBeInstanceOf(LoginPageError);
    expect((await signInThroughLoginPage(newSession(), name, newPassword)).username).toBe(name);
  });

  it('My account has a Change password button too', async () => {
    const { user } = await signedInAsNewAdmin('/account');
    const card = await screen.findByRole('region', { name: 'Password' }, slow);
    await user.click(within(card).getByRole('button', { name: 'Change password' }));
    expect(screen.getByRole('dialog', { name: 'Change password' })).toBeInTheDocument();
    await user.click(within(screen.getByRole('dialog', { name: 'Change password' })).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog', { name: 'Change password' })).not.toBeInTheDocument();
  });

  it('Your sessions lists the browser and the phone, and ending the phone one signs it out', async () => {
    const name = uniqueName('sess');
    await registerClinician(await adminSession(), name, 'admin');
    // The phone signs in first (the test-only password grant does not ask for a new password), then this browser.
    const phone = await mobileLogin(name, strongPassword, `dev-${name}`);
    expect(phone.status).toBe(200);
    const session = newSession();
    const user = renderDashboard(session, '/account');
    await signInOnScreen(user, session, name, strongPassword, undefined, ownPassword);

    const card = await screen.findByRole('region', { name: 'Your sessions' }, slow);
    expect(await screen.findByText('Your sessions · 2', {}, slow)).toBeInTheDocument();
    expect(within(card).getByText('Admin dashboard')).toBeInTheDocument();
    expect(within(card).getByText(`· dev-${name}`, { exact: false })).toBeInTheDocument();

    await user.click(within(card).getByRole('button', { name: /^End session: Mobile app on / }));
    const dialog = screen.getByRole('dialog', { name: 'End this session?' });
    await user.click(within(dialog).getByRole('button', { name: 'End session' }));
    expect(await findToast(`Mobile app on dev-${name} is signed out.`)).toBeInTheDocument();
    expect(await screen.findByText('Your sessions · 1', {}, slow)).toBeInTheDocument();
    expect(within(card).queryByText(`· dev-${name}`, { exact: false })).not.toBeInTheDocument();
    // The server agrees: only this browser's session is left.
    expect(await session.authorized((t) => session.api.mySessions(t))).toHaveLength(1);
  });

  it('End all sessions signs you out here too and says why', async () => {
    const { user } = await signedInAsNewAdmin('/account');
    await screen.findByText('Your sessions · 1', {}, slow);
    await user.click(screen.getByRole('button', { name: 'End all sessions' }));
    const dialog = screen.getByRole('dialog', { name: 'End all your sessions?' });
    await user.click(within(dialog).getByRole('button', { name: 'End all sessions' }));
    expect(await screen.findByRole('button', { name: 'Sign in' }, slow)).toBeInTheDocument();
    expect(await findToast('All your sessions have ended. Sign in again here.')).toBeInTheDocument();
  });

  it('links to My account, and signs you out', async () => {
    const user = await signedIn();
    await user.click(screen.getByRole('button', { name: 'Your profile' }));
    await user.click(within(await screen.findByRole('dialog', { name: 'Your profile' })).getByRole('button', { name: 'My account' }));
    expect(await screen.findByRole('heading', { name: 'My account' }, slow)).toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: 'Your profile' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Your profile' }));
    const panel = await screen.findByRole('dialog', { name: 'Your profile' });
    await user.click(within(panel).getByRole('button', { name: 'Sign out' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument(), slow);
  });
});
