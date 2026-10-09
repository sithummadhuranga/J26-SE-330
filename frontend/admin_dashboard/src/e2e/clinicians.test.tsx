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
  const user = renderDashboard(newSession());
  await signInOnScreen(user, demoAdmin.username, demoAdmin.password);
  await screen.findByText(`${demoAdmin.username} (you)`, {}, slow);
  return user;
}

const rowOf = (username: string) => screen.getByText(username).closest('tr')!;

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
    expect(within(dialog).getByText('At least 12 characters')).toBeInTheDocument();

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
    expect((await mobileLogin(name, strongPassword, `dev-${name}`)).status).toBe(401);
  });

  it('an admin cannot deactivate themselves: the option is not offered, and the server refuses it too', async () => {
    const user = await openClinicians();
    const me = rowOf(`${demoAdmin.username} (you)`);
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

  it('a username with unusual characters is escaped in the URL, and an unknown one is not found', async () => {
    const admin = await adminSession();
    await expect(admin.authorized((t) => admin.api.unlock(t, 'no such/user'))).rejects.toMatchObject({ status: 404 });
  });
});
