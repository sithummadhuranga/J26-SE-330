import { screen, waitFor, within } from '@testing-library/react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  adminSession,
  demoAdmin,
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

describe('audit log (real audit trail)', () => {
  it('shows what really happened, and the search, typo search and "Failures only" filter work on it', async () => {
    const admin = await adminSession();
    const nurse = uniqueName('audit');
    await registerClinician(admin, nurse);
    await mobileLogin(nurse, 'wrong-password-1', `dev-${nurse}`);
    await mobileLogin(nurse, strongPassword, `dev-${nurse}`);

    const user = renderDashboard(newSession(), '/audit');
    await signInOnScreen(user, demoAdmin.username, demoAdmin.password);
    await screen.findByRole('table', {}, slow);

    // Search for this run's clinician: created by the admin, one wrong password, one sign-in.
    await user.type(screen.getByLabelText('Search audit log'), nurse);
    // Looked up each time: the table is replaced by "No matching events" while a search matches nothing.
    const table = () => screen.getByRole('table');
    await waitFor(() => expect(within(table()).getAllByRole('row')).toHaveLength(4), slow);
    expect(within(table()).getByText('Account created')).toBeInTheDocument();
    expect(within(table()).getByText('Wrong password')).toBeInTheDocument();
    expect(within(table()).getByText(demoAdmin.username)).toBeInTheDocument();
    expect(screen.getByText(/^3 of \d+ events$/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Failures only' }));
    expect(within(table()).getAllByRole('row')).toHaveLength(2);
    expect(within(table()).getByText('Wrong password')).toBeInTheDocument();

    // A misspelled search still finds it.
    await user.click(screen.getByRole('button', { name: 'All events' }));
    await user.clear(screen.getByLabelText('Search audit log'));
    await user.type(screen.getByLabelText('Search audit log'), `wrnog pasword ${nurse}`);
    expect(within(table()).getAllByRole('row')).toHaveLength(2);

    // Showing more events asks the server for up to 250 of them.
    const total = () => Number(/of (\d+) events$/.exec(screen.getByText(/of \d+ events$/).textContent ?? '')?.[1]);
    const before = total();
    await user.selectOptions(screen.getByLabelText('How many events'), '250');
    expect(screen.getByLabelText('How many events')).toHaveValue('250');
    await waitFor(() => expect(total()).toBeGreaterThanOrEqual(before), slow);
    expect(total()).toBeLessThanOrEqual(250);
  });
});
