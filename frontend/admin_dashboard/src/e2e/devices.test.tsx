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
} from './backend';

beforeAll(requireBackend);
afterAll(cleanUp);

describe('devices (real identity service, Sync Gateway and database)', () => {
  it('a phone that signed in is listed; revoking it is saved and the phone can no longer sign in', async () => {
    const admin = await adminSession();
    const nurse = uniqueName('dev');
    const deviceId = `dev-${nurse}`;
    await registerClinician(admin, nurse);
    expect((await mobileLogin(nurse, strongPassword, deviceId)).status).toBe(200); // registers the phone

    const user = renderDashboard(newSession(), '/devices');
    await signInOnScreen(user, demoAdmin.username, demoAdmin.password);
    const cell = await screen.findByText(deviceId, {}, slow);
    const row = cell.closest('tr')!;
    expect(within(row).getByText('Active')).toBeInTheDocument();
    expect(within(row).getByText(nurse)).toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: `Revoke ${deviceId}` }));
    await user.click(within(screen.getByRole('dialog', { name: `Revoke ${deviceId}?` })).getByRole('button', { name: 'Revoke device' }));

    expect(await findToast(`${deviceId} can no longer sign in or sync.`)).toBeInTheDocument();
    await waitFor(() => expect(within(screen.getByText(deviceId).closest('tr')!).getByText('Revoked')).toBeInTheDocument(), slow);
    const saved = (await admin.authorized((t) => admin.api.devices(t))).find((d) => d.deviceId === deviceId);
    expect(saved?.revokedAt).not.toBeNull();
    expect(await mobileLogin(nurse, strongPassword, deviceId)).toMatchObject({ status: 401, code: 'DEVICE_NOT_ALLOWED' });

    // Revoking twice is refused by the server.
    await expect(admin.authorized((t) => admin.api.revokeDevice(t, deviceId))).rejects.toMatchObject({
      code: 'DEVICE_ALREADY_REVOKED',
    });
  });
});
