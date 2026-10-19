import { screen, waitFor, within } from '@testing-library/react';
import { beforeAll, describe, expect, it } from 'vitest';
import { demoAdmin, newSession, renderDashboard, requireBackend, signInOnScreen, slow } from './backend';

beforeAll(requireBackend);

async function signedIn() {
  const session = newSession();
  const user = renderDashboard(session);
  await signInOnScreen(user, session, demoAdmin.username, demoAdmin.password);
  await screen.findByRole('heading', { name: 'Clinicians' }, slow);
  return user;
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
