import { screen, waitFor, within } from '@testing-library/react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  adminSession,
  demoAdmin,
  findToast,
  mobileLogin,
  newSession,
  registerClinician,
  renderDashboard,
  requireBackend,
  signInOnScreen,
  slow,
  strongPassword,
  uniqueName,
  cleanUp,
  track,
} from './backend';

beforeAll(requireBackend);
afterAll(cleanUp);

async function openClinicians() {
  const session = newSession();
  const user = renderDashboard(session);
  await signInOnScreen(user, session, demoAdmin.username, demoAdmin.password);
  await screen.findByRole('button', { name: 'Next page' }, slow); // the table and its pager are shown
  return user;
}

const rowOf = (username: string) => screen.getByText(username).closest('tr')!;

/** The row with [text], moving through the pages with the arrows (the demo admin, the oldest, is on the last). */
async function rowOnSomePage(user: ReturnType<typeof renderDashboard>, text: string) {
  for (;;) {
    const cell = screen.queryByText(text);
    if (cell) return cell.closest('tr')!;
    const next = screen.getByRole('button', { name: 'Next page' });
    if ((next as HTMLButtonElement).disabled) throw new Error(`"${text}" is on no page`);
    await user.click(next);
  }
}

describe('clinicians (real identity service and database)', () => {
  it('registering a clinician saves them, shows them as the first row, and they can sign in on the app', async () => {
    const user = await openClinicians();
    const name = uniqueName('reg');
    track(name);

    await user.click(screen.getByRole('button', { name: 'Register clinician' }));
    const dialog = screen.getByRole('dialog', { name: 'Register clinician' });
    await user.type(within(dialog).getByLabelText('Full name'), 'E2E Registered');
    await user.type(within(dialog).getByLabelText('Username'), name);
    await user.type(within(dialog).getByLabelText('Initial password'), strongPassword);
    await user.selectOptions(within(dialog).getByLabelText('Role'), 'wound_specialist');
    await user.click(within(dialog).getByRole('button', { name: 'Register' }));

    expect(await findToast(`${name} can now sign in on the mobile app.`)).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByRole('row')[1]).toBe(rowOf(name)), slow);
    expect(within(rowOf(name)).getByText('Wound specialist')).toBeInTheDocument();

    // Saved in the database: a separate session reads it back, and the new account works.
    const admin = await adminSession();
    const saved = (await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name);
    expect(saved).toMatchObject({ fullName: 'E2E Registered', role: 'wound_specialist', active: true, locked: false });
    expect((await mobileLogin(name, strongPassword, `dev-${name}`)).status).toBe(200);
  });

  it('the form checks the rules before sending, and a taken username is refused by the server', async () => {
    const user = await openClinicians();
    await user.click(screen.getByRole('button', { name: 'Register clinician' }));
    const dialog = screen.getByRole('dialog', { name: 'Register clinician' });

    await user.type(within(dialog).getByLabelText('Username'), 'Bad Name!');
    await user.type(within(dialog).getByLabelText('Initial password'), 'short');
    await user.click(within(dialog).getByRole('button', { name: 'Register' }));
    expect(within(dialog).getByText('Enter the full name')).toBeInTheDocument();
    expect(within(dialog).getByText('Use 3–64 of a–z, 0–9, . _ -')).toBeInTheDocument();
    expect(within(dialog).getByText('At least 8 characters')).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText('Full name'), 'Taken Name');
    await user.clear(within(dialog).getByLabelText('Username'));
    await user.type(within(dialog).getByLabelText('Username'), demoAdmin.username);
    await user.clear(within(dialog).getByLabelText('Initial password'));
    await user.type(within(dialog).getByLabelText('Initial password'), strongPassword);
    await user.click(within(dialog).getByRole('button', { name: 'Register' }));

    expect(await findToast('That username is already in use. Choose another one.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Register clinician' })).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Username')).toHaveValue(demoAdmin.username);
  });

  it('deactivating asks first, saves the change, and the clinician can no longer sign in', async () => {
    const admin = await adminSession();
    const name = uniqueName('deact');
    await registerClinician(admin, name);
    const user = await openClinicians();

    await user.click(within(rowOf(name)).getByRole('button', { name: `Actions for ${name}` }));
    await user.click(screen.getByRole('menuitem', { name: 'Deactivate' }));
    const dialog = screen.getByRole('dialog', { name: `Deactivate ${name}?` });
    expect((await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name)?.active).toBe(true);
    await user.click(within(dialog).getByRole('button', { name: 'Deactivate' }));

    expect(await findToast(`Deactivated ${name}.`)).toBeInTheDocument();
    await waitFor(() => expect(within(rowOf(name)).getByText('Deactivated')).toBeInTheDocument(), slow);
    expect((await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name)?.active).toBe(false);
    expect(await mobileLogin(name, strongPassword, `dev-${name}`)).toMatchObject({ status: 400, code: 'INVALID_CREDENTIALS' });
  });

  it('an admin cannot deactivate themselves: the option is not offered, and the server refuses it too', async () => {
    const user = await openClinicians();
    const me = await rowOnSomePage(user, `${demoAdmin.username} (you)`);
    const actions = within(me).queryByRole('button', { name: `Actions for ${demoAdmin.username}` });
    if (actions) {
      await user.click(actions);
      expect(screen.queryByRole('menuitem', { name: 'Deactivate' })).not.toBeInTheDocument();
    }
    const admin = await adminSession();
    await expect(admin.authorized((t) => admin.api.deactivate(t, demoAdmin.username))).rejects.toMatchObject({
      code: 'CANNOT_DEACTIVATE_SELF',
    });
  });

  it('resetting a password unlocks the clinician, and only the new password works', async () => {
    const admin = await adminSession();
    const name = uniqueName('reset');
    await registerClinician(admin, name);
    expect((await mobileLogin(name, strongPassword, `dev-${name}`)).status).toBe(200);
    for (let i = 0; i < 5; i++) await mobileLogin(name, 'wrong-password-123', `dev-${name}`);
    expect((await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name)?.locked).toBe(true);
    const user = await openClinicians();

    await user.click(within(rowOf(name)).getByRole('button', { name: `Actions for ${name}` }));
    await user.click(screen.getByRole('menuitem', { name: 'Reset password' }));
    const dialog = screen.getByRole('dialog', { name: `Reset password for ${name}` });
    await user.type(within(dialog).getByLabelText('New password'), 'short');
    await user.click(within(dialog).getByRole('button', { name: 'Reset password' }));
    expect(within(dialog).getByText('At least 8 characters')).toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText('New password'));
    await user.type(within(dialog).getByLabelText('New password'), 'New-E2e-Pass-2026!');
    await user.click(within(dialog).getByRole('button', { name: 'Reset password' }));

    expect(await findToast(`${name} is signed out, signs in with the new password, then chooses their own.`)).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(within(rowOf(name)).getByText('New password due')).toBeInTheDocument(), slow);
    expect((await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name)?.locked).toBe(false);
    expect(await mobileLogin(name, strongPassword, `dev-${name}`)).toMatchObject({ status: 400, code: 'INVALID_CREDENTIALS' });
    expect((await mobileLogin(name, 'New-E2e-Pass-2026!', `dev-${name}`)).status).toBe(200);
  });

  it('reactivating a deactivated clinician asks first, saves it, and they can sign in again', async () => {
    const admin = await adminSession();
    const name = uniqueName('react');
    await registerClinician(admin, name);
    await admin.authorized((t) => admin.api.deactivate(t, name));
    const user = await openClinicians();

    await user.click(within(rowOf(name)).getByRole('button', { name: `Actions for ${name}` }));
    expect(screen.queryByRole('menuitem', { name: 'Deactivate' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Reactivate' }));
    const dialog = screen.getByRole('dialog', { name: `Reactivate ${name}?` });
    await user.click(within(dialog).getByRole('button', { name: 'Reactivate' }));

    expect(await findToast(`Reactivated ${name}.`)).toBeInTheDocument();
    await waitFor(() => expect(within(rowOf(name)).getByText('Active')).toBeInTheDocument(), slow);
    expect((await admin.authorized((t) => admin.api.clinicians(t))).find((c) => c.username === name)?.active).toBe(true);
    expect((await mobileLogin(name, strongPassword, `dev-${name}`)).status).toBe(200);
    await expect(admin.authorized((t) => admin.api.reactivate(t, name))).rejects.toMatchObject({ code: 'ALREADY_ACTIVE' });
  });

  it('an admin cannot reset their own password: the option is not offered, and the server refuses it too', async () => {
    const user = await openClinicians();
    const actions = within(await rowOnSomePage(user, `${demoAdmin.username} (you)`)).queryByRole('button', {
      name: `Actions for ${demoAdmin.username}`,
    });
    if (actions) {
      await user.click(actions);
      expect(screen.queryByRole('menuitem', { name: 'Reset password' })).not.toBeInTheDocument();
    }
    const admin = await adminSession();
    await expect(
      admin.authorized((t) => admin.api.resetPassword(t, demoAdmin.username, 'Another-Pass-2026!')),
    ).rejects.toMatchObject({ code: 'CANNOT_RESET_OWN_PASSWORD' });
  });

  it('the table shows 10 newest-first rows a page, and the arrows move between pages', async () => {
    const admin = await adminSession();
    const total = (await admin.authorized((t) => admin.api.clinicians(t))).length;
    if (total <= 10) await registerClinician(admin, uniqueName('page')); // make sure there is a second page
    const user = await openClinicians();
    const pager = screen.getByRole('navigation', { name: 'clinicians pages' });

    expect(screen.getAllByRole('row')).toHaveLength(11); // header + 10
    expect(within(pager).getByText(/^1–10 of \d+$/)).toBeInTheDocument();
    expect(within(pager).getByRole('button', { name: 'First page' })).toBeDisabled();
    expect(within(pager).getByRole('button', { name: 'Previous page' })).toBeDisabled();
    const firstOnPage1 = screen.getAllByRole('row')[1]!.textContent;

    await user.click(within(pager).getByRole('button', { name: 'Next page' }));
    expect(within(pager).getByText(/^11–\d+ of \d+$/)).toBeInTheDocument();
    expect(within(pager).getByText(/^Page 2 of \d+$/)).toBeInTheDocument();
    expect(screen.getAllByRole('row')[1]!.textContent).not.toBe(firstOnPage1);

    await user.click(within(pager).getByRole('button', { name: 'Last page' }));
    expect(within(pager).getByRole('button', { name: 'Next page' })).toBeDisabled();
    expect(screen.getByText(`${demoAdmin.username} (you)`)).toBeInTheDocument(); // the oldest account comes last

    await user.click(within(pager).getByRole('button', { name: 'First page' }));
    expect(screen.getAllByRole('row')[1]!.textContent).toBe(firstOnPage1);
  });

  it('a username with unusual characters is escaped in the URL, and an unknown one is not found', async () => {
    const admin = await adminSession();
    await expect(admin.authorized((t) => admin.api.unlock(t, 'no such/user'))).rejects.toMatchObject({ status: 404 });
  });
});
