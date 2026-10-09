import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import type { DeviceSummary } from '../core/models';
import { Centered, LoadError, PageHeader, Spinner, StatusChip, TableCard } from '../ui/components';
import { useConfirm } from '../ui/confirmContext';
import { useLoadFailureToast, useToast } from '../ui/toastContext';

const devicesKey = ['devices'];

/** Registered phones; revoking ends their sessions within 30 s but leaves unsynced data on the phone. */
export function DevicesPage() {
  const session = useSession();
  const toast = useToast();
  const confirm = useConfirm();
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: devicesKey,
    queryFn: () => session.authorized((t) => session.api.devices(t)),
  });
  useLoadFailureToast("Couldn't load devices", query.error);
  const reload = () => void queryClient.invalidateQueries({ queryKey: devicesKey });

  const revoke = async (d: DeviceSummary) => {
    const yes = await confirm({
      title: `Revoke ${d.deviceId}?`,
      message:
        'Use this for a lost or stolen phone. It is signed out, cannot sign in or sync again, and this ' +
        'cannot be undone. Records not yet synced from it stay on the phone.',
      action: 'Revoke device',
      destructive: true,
    });
    if (!yes) return;
    try {
      await session.authorized((t) => session.api.revokeDevice(t, d.deviceId));
      toast.success('Device revoked', `${d.deviceId} can no longer sign in or sync.`);
    } catch (e) {
      toast.error(`Couldn't revoke ${d.deviceId}`, e);
    }
    reload();
  };

  return (
    <>
      <PageHeader
        title="Devices"
        subtitle="Phones registered at your facility. Revoke a lost or stolen one."
        actions={
          <button type="button" className="btn btn--outline" onClick={reload}>
            Refresh
          </button>
        }
      />
      {query.isError ? (
        <LoadError error={query.error} onRetry={reload} />
      ) : query.isPending ? (
        <Centered>
          <Spinner />
        </Centered>
      ) : query.data.length === 0 ? (
        <Centered>No devices have signed in yet.</Centered>
      ) : (
        <TableCard>
          <thead>
            <tr>
              <th>Device</th>
              <th>Status</th>
              <th>Last user</th>
              <th>Last seen</th>
              <th className="table__num">Sessions</th>
              <th>Registered</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {query.data.map((d) => (
              <tr key={d.deviceId}>
                <td className="table__mono">{d.deviceId}</td>
                <td>
                  {d.revokedAt ? (
                    <StatusChip label="Revoked" tone="error" title={`Revoked ${formatDateTime(d.revokedAt)}`} />
                  ) : (
                    <StatusChip label="Active" tone="teal" />
                  )}
                </td>
                <td>{d.lastUsername ?? '—'}</td>
                <td>{formatDateTime(d.lastSeenAt)}</td>
                <td className="table__num">{d.activeSessions}</td>
                <td>{formatDateTime(d.registeredAt)}</td>
                <td className="table__actions">
                  {!d.revokedAt && (
                    <button
                      type="button"
                      className="btn btn--text btn--danger-text"
                      aria-label={`Revoke ${d.deviceId}`}
                      onClick={() => void revoke(d)}
                    >
                      Revoke
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </TableCard>
      )}
    </>
  );
}
