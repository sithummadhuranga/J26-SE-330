import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useAdmin, useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import { roleLabel, type ClinicianSummary } from '../core/models';
import { ActionsMenu, Centered, LoadError, PageHeader, Spinner, StatusChip, TableCard } from '../ui/components';
import { useConfirm } from '../ui/confirmContext';
import { useLoadFailureToast, useToast } from '../ui/toastContext';
import { RegisterClinicianDialog } from './RegisterClinicianDialog';

const cliniciansKey = ['clinicians'];

type Action = 'unlock' | 'resetMfa' | 'deactivate';

/** Newest first, so a clinician who was just registered is the top row (the server sorts by username). */
const newestFirst = (a: ClinicianSummary, b: ClinicianSummary) =>
  b.createdAt.localeCompare(a.createdAt) || a.username.localeCompare(b.username);

/** Facility clinicians: register, unlock, reset MFA or deactivate (all audited). */
export function CliniciansPage() {
  const session = useSession();
  const me = useAdmin()?.username;
  const toast = useToast();
  const confirm = useConfirm();
  const queryClient = useQueryClient();
  const [registering, setRegistering] = useState(false);
  const [justAdded, setJustAdded] = useState<string | null>(null);

  // The highlight on a newly registered row fades after a few seconds.
  useEffect(() => {
    if (!justAdded) return;
    const timer = setTimeout(() => setJustAdded(null), 6000);
    return () => clearTimeout(timer);
  }, [justAdded]);

  const query = useQuery({
    queryKey: cliniciansKey,
    queryFn: () => session.authorized((t) => session.api.clinicians(t)),
  });
  useLoadFailureToast("Couldn't load clinicians", query.error);
  const reload = () => void queryClient.invalidateQueries({ queryKey: cliniciansKey });

  const run = async (action: Action, c: ClinicianSummary) => {
    const api = session.api;
    const plan = {
      unlock: {
        title: `Unlock ${c.username}?`,
        message: `Clears the failed sign-in count so ${c.fullName} can sign in again.`,
        label: 'Unlock',
        destructive: false,
        done: `Unlocked ${c.username}.`,
        failed: `Couldn't unlock ${c.username}`,
        call: (t: string) => api.unlock(t, c.username),
      },
      resetMfa: {
        title: `Reset MFA for ${c.username}?`,
        message:
          `${c.fullName} will sign in with password only until they enroll a new authenticator. ` +
          'Do this only after confirming who is asking.',
        label: 'Reset MFA',
        destructive: true,
        done: `MFA reset for ${c.username}.`,
        failed: `Couldn't reset MFA for ${c.username}`,
        call: (t: string) => api.resetMfa(t, c.username),
      },
      deactivate: {
        title: `Deactivate ${c.username}?`,
        message:
          `${c.fullName} is signed out on every device and can no longer sign in. ` +
          'Records they captured are kept.',
        label: 'Deactivate',
        destructive: true,
        done: `Deactivated ${c.username}.`,
        failed: `Couldn't deactivate ${c.username}`,
        call: (t: string) => api.deactivate(t, c.username),
      },
    }[action];

    const yes = await confirm({
      title: plan.title,
      message: plan.message,
      action: plan.label,
      destructive: plan.destructive,
    });
    if (!yes) return;
    try {
      await session.authorized(plan.call);
      toast.success(plan.done);
    } catch (e) {
      toast.error(plan.failed, e);
    }
    reload();
  };

  const onRegistered = (username: string) => {
    setRegistering(false);
    setJustAdded(username);
    toast.success('Clinician registered', `${username} can now sign in on the mobile app.`);
    reload();
  };

  return (
    <>
      <PageHeader
        title="Clinicians"
        subtitle="Everyone who can sign in at your facility."
        actions={
          <>
            <button type="button" className="btn btn--outline" onClick={reload}>
              Refresh
            </button>
            <button type="button" className="btn btn--primary" onClick={() => setRegistering(true)}>
              Register clinician
            </button>
          </>
        }
      />
      {query.isError ? (
        <LoadError error={query.error} onRetry={reload} />
      ) : query.isPending ? (
        <Centered>
          <Spinner />
        </Centered>
      ) : query.data.length === 0 ? (
        <Centered>No clinicians yet.</Centered>
      ) : (
        <TableCard>
          <thead>
            <tr>
              <th>Name</th>
              <th>Username</th>
              <th>Role</th>
              <th>Status</th>
              <th>Created</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {[...query.data].sort(newestFirst).map((c) => {
              const isSelf = c.username === me;
              const items = [
                ...(c.locked ? [{ label: 'Unlock', onSelect: () => void run('unlock', c) }] : []),
                ...(c.mfaEnabled ? [{ label: 'Reset MFA', onSelect: () => void run('resetMfa', c) }] : []),
                ...(c.active && !isSelf ? [{ label: 'Deactivate', danger: true, onSelect: () => void run('deactivate', c) }] : []),
              ];
              return (
                <tr key={c.clinicianId} className={c.username === justAdded ? 'row--new' : undefined}>
                  <td>{c.fullName}</td>
                  <td>
                    {c.username}
                    {isSelf && ' (you)'}
                  </td>
                  <td>{roleLabel(c.role)}</td>
                  <td>
                    <span className="chips">
                      {c.active ? <StatusChip label="Active" tone="teal" /> : <StatusChip label="Deactivated" tone="error" />}
                      {c.locked && <StatusChip label="Locked" tone="error" />}
                      {c.mfaEnabled && <StatusChip label="MFA on" tone="blue" />}
                    </span>
                  </td>
                  <td>{formatDateTime(c.createdAt)}</td>
                  <td className="table__actions">
                    {items.length > 0 && <ActionsMenu label={`Actions for ${c.username}`} items={items} />}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </TableCard>
      )}
      {registering && <RegisterClinicianDialog onClose={() => setRegistering(false)} onRegistered={onRegistered} />}
    </>
  );
}
