import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useAdmin, useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import { roleLabel, type ClinicianSummary } from '../core/models';
import { usePaged } from '../core/paging';
import { cliniciansKey } from '../core/queryKeys';
import {
  ActionsMenu,
  Centered,
  EmptyState,
  HeroAction,
  InitialsAvatar,
  LoadError,
  PageHeader,
  SectionTitle,
  Spinner,
  StatusChip,
  TableCard,
} from '../ui/components';
import { PeopleIcon, PlusIcon } from '../ui/icons';
import { Pagination } from '../ui/Pagination';
import { useConfirm } from '../ui/confirmContext';
import { useLoadFailureToast, useToast } from '../ui/toastContext';
import { RegisterClinicianDialog } from './RegisterClinicianDialog';
import { ResetPasswordDialog } from './ResetPasswordDialog';

type Action = 'unlock' | 'resetMfa' | 'deactivate' | 'reactivate';

/** Newest first, so a clinician who was just registered ends up at the top. The server sorts by username. */
const newestFirst = (a: ClinicianSummary, b: ClinicianSummary) =>
  b.createdAt.localeCompare(a.createdAt) || a.username.localeCompare(b.username);

/** The facility's clinicians. You can register, unlock, reset a password or MFA, or deactivate. It's all audited. */
export function CliniciansPage() {
  const session = useSession();
  const me = useAdmin()?.username;
  const toast = useToast();
  const confirm = useConfirm();
  const queryClient = useQueryClient();
  const [registering, setRegistering] = useState(false);
  const [justAdded, setJustAdded] = useState<string | null>(null);
  const [resetting, setResetting] = useState<ClinicianSummary | null>(null);

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
  const sorted = useMemo(() => [...(query.data ?? [])].sort(newestFirst), [query.data]);
  const paged = usePaged(sorted);

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
      reactivate: {
        title: `Reactivate ${c.username}?`,
        message: `${c.fullName} can sign in again with their current password. Their old sessions stay ended.`,
        label: 'Reactivate',
        destructive: false,
        done: `Reactivated ${c.username}.`,
        failed: `Couldn't reactivate ${c.username}`,
        call: (t: string) => api.reactivate(t, c.username),
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
    paged.setPage(0); // the new clinician is the first row of the first page
    toast.success('Clinician registered', `${username} can now sign in on the mobile app.`);
    reload();
  };

  const onPasswordReset = () => {
    toast.success('Password reset', `${resetting?.username} is signed out, signs in with the new password, then chooses their own.`);
    setResetting(null);
    reload();
  };

  return (
    <>
      <PageHeader
        title="Clinicians"
        subtitle="Everyone who can sign in at your facility."
        actions={
          <button type="button" className="btn btn--outline" onClick={reload}>
            Refresh
          </button>
        }
      />
      <div className="page-body">
        <HeroAction
          label="Register clinician"
          description="Add a nurse, wound specialist or admin to your facility"
          icon={<PlusIcon size={22} />}
          onClick={() => setRegistering(true)}
        />
        {query.isSuccess && query.data.length > 0 && (
          <SectionTitle
            trailing={
              <span className="section-title__count">
                {query.data.filter((c) => c.active).length} active · {query.data.length} in total
              </span>
            }
          >
            Accounts
          </SectionTitle>
        )}
      </div>
      {query.isError ? (
        <LoadError error={query.error} onRetry={reload} />
      ) : query.isPending ? (
        <Centered>
          <Spinner />
        </Centered>
      ) : query.data.length === 0 ? (
        <EmptyState
          icon={<PeopleIcon size={24} />}
          title="No clinicians yet"
          message="Register the nurses and wound specialists who will use the mobile app."
        />
      ) : (
        <TableCard footer={<Pagination paged={paged} noun="clinicians" />}>
          <thead>
            <tr>
              <th>Clinician</th>
              <th>Username</th>
              <th>Status</th>
              <th>Created</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {paged.rows.map((c) => {
              const isSelf = c.username === me;
              const items = [
                ...(c.locked ? [{ label: 'Unlock', onSelect: () => void run('unlock', c) }] : []),
                ...(c.active && !isSelf ? [{ label: 'Reset password', onSelect: () => setResetting(c) }] : []),
                ...(c.mfaEnabled ? [{ label: 'Reset MFA', onSelect: () => void run('resetMfa', c) }] : []),
                ...(c.active && !isSelf ? [{ label: 'Deactivate', danger: true, onSelect: () => void run('deactivate', c) }] : []),
                ...(!c.active ? [{ label: 'Reactivate', onSelect: () => void run('reactivate', c) }] : []),
              ];
              return (
                <tr key={c.clinicianId} className={c.username === justAdded ? 'row--new' : undefined}>
                  <td>
                    <span className="person">
                      <InitialsAvatar name={c.fullName} />
                      <span className="person__text">
                        <span className="person__name">{c.fullName}</span>
                        <span className="person__sub">{roleLabel(c.role)}</span>
                      </span>
                    </span>
                  </td>
                  <td>
                    {c.username}
                    {isSelf && ' (you)'}
                  </td>
                  <td>
                    <span className="chips">
                      {c.active ? <StatusChip label="Active" tone="teal" /> : <StatusChip label="Deactivated" tone="error" />}
                      {c.locked && <StatusChip label="Locked" tone="error" />}
                      {c.mfaEnabled && <StatusChip label="MFA on" tone="blue" />}
                      {c.active && c.mustChangePassword && (
                        <StatusChip label="New password due" tone="muted" title="Chooses their own password at the next sign-in" />
                      )}
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
      {resetting && (
        <ResetPasswordDialog clinician={resetting} onClose={() => setResetting(null)} onReset={onPasswordReset} />
      )}
    </>
  );
}
