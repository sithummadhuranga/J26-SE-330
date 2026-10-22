import { screen, within } from '@testing-library/react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  adminSession,
  cleanUp,
  findToast,
  LoginPageError,
  newSession,
  ownPassword,
  registerClinician,
  renderDashboard,
  requireBackend,
  signInOnScreen,
  signInThroughLoginPage,
  slow,
  strongPassword,
  totpCode,
  uniqueName,
} from './backend';

beforeAll(requireBackend);
afterAll(cleanUp);

/** A new admin, so turning MFA on never changes the demo admin the other tests sign in as. */
async function newAdminOnAccountPage() {
  const name = uniqueName('mfa');
  await registerClinician(await adminSession(), name, 'admin');
  const session = newSession();
  const user = renderDashboard(session, '/account');
  await signInOnScreen(user, session, name, strongPassword, undefined, ownPassword);
  await screen.findByRole('heading', { name: 'My account' }, slow);
  return { user, name };
}

const step = () => Math.floor(Date.now() / 30_000);

describe('my account: two-step sign-in (real identity service)', () => {
  it('an admin turns on MFA with the QR code, and the next sign-in asks for a code', async () => {
    const { user, name } = await newAdminOnAccountPage();
    const card = await screen.findByRole('region', { name: 'Two-step sign-in' }, slow);
    expect(within(card).getByText('Off')).toBeInTheDocument();

    await user.click(within(card).getByRole('button', { name: 'Set up two-step sign-in' }));
    expect(await within(card).findByAltText('QR code for your authenticator app', {}, slow)).toHaveAttribute(
      'src',
      expect.stringMatching(/^data:image\/svg\+xml/),
    );
    const secret = within(card).getByTestId('mfa-secret').textContent!;

    await user.type(within(card).getByLabelText('Code from your app'), totpCode(secret));
    await user.click(within(card).getByRole('button', { name: 'Turn on' }));
    expect(await findToast('Two-step sign-in is on')).toBeInTheDocument();
    expect(await within(card).findByText('On', {}, slow)).toBeInTheDocument();

    // The password on its own no longer gets you in, but with a fresh code from the app it does.
    await expect(signInThroughLoginPage(newSession(), name, ownPassword)).rejects.toBeInstanceOf(LoginPageError);
    const admin = await signInThroughLoginPage(newSession(), name, ownPassword, totpCode(secret, step() + 1));
    expect(admin.username).toBe(name);

    // The clinicians list shows it to every admin of the facility.
    const demo = await adminSession();
    expect((await demo.authorized((t) => demo.api.clinicians(t))).find((c) => c.username === name)?.mfaEnabled).toBe(true);
  });

  it('a wrong code keeps MFA off and says why', async () => {
    const { user } = await newAdminOnAccountPage();
    const card = await screen.findByRole('region', { name: 'Two-step sign-in' }, slow);
    await user.click(within(card).getByRole('button', { name: 'Set up two-step sign-in' }));
    await within(card).findByTestId('mfa-secret', {}, slow);

    await user.type(within(card).getByLabelText('Code from your app'), '12');
    await user.click(within(card).getByRole('button', { name: 'Turn on' }));
    expect(within(card).getByText('Enter the 6-digit code from your app')).toBeInTheDocument();

    await user.clear(within(card).getByLabelText('Code from your app'));
    await user.type(within(card).getByLabelText('Code from your app'), '000000');
    await user.click(within(card).getByRole('button', { name: 'Turn on' }));
    expect(await findToast(/That code is not right/)).toBeInTheDocument();
    expect(within(card).getByLabelText('Code from your app')).toBeInTheDocument();
  });
});
