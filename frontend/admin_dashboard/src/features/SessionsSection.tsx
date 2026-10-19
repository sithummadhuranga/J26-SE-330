import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import type { MySession } from '../core/models';
import { usePaged } from '../core/paging';
import { sessionsKey } from '../core/queryKeys';
import { Centered, EmptyState, IconTile, LoadError, SectionTitle, Spinner } from '../ui/components';
import { useConfirm } from '../ui/confirmContext';
import { MonitorIcon, PhoneIcon } from '../ui/icons';
import { Pagination } from '../ui/Pagination';
import { useLoadFailureToast, useToast } from '../ui/toastContext';

const clientLabel = (s: MySession) => (s.client === 'mobile' ? 'Mobile app' : 'Admin dashboard');

/**
 * "Your sessions" on My account (plan: profile management, step 8): where you are signed in, with a way to end one
 * session or all of them. The server can't tell which session is this browser, so none is marked as "this one".
 */
export function SessionsSection() {
  const session = useSession();
  const toast = useToast();
  const confirm = useConfirm();
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: sessionsKey,
    queryFn: () => session.authorized((t) => session.api.mySessions(t)),
  });
  useLoadFailureToast("Couldn't load your sessions", query.error);
  const reload = () => void queryClient.invalidateQueries({ queryKey: sessionsKey });

  const endOne = async (s: MySession) => {
    const where = s.deviceId ? `${clientLabel(s)} on ${s.deviceId}` : clientLabel(s);
    const yes = await confirm({
      title: 'End this session?',
      message:
        `${where}, signed in ${formatDateTime(s.signedInAt)}, is signed out at its next refresh, within 15 minutes. ` +
        'If it is this browser, you are signed out here too.',
      action: 'End session',
      destructive: true,
    });
    if (!yes) return;
    try {
      await session.authorized((t) => session.api.endSession(t, s.sessionId));
      toast.success('Session ended', `${where} is signed out.`);
    } catch (e) {
      toast.error("Couldn't end the session", e);
    }
    reload();
  };

  const endAll = async () => {
    const yes = await confirm({
      title: 'End all your sessions?',
      message: 'Every browser and phone signed in as you is signed out, this one included. You then sign in again here.',
      action: 'End all sessions',
      destructive: true,
    });
    if (!yes) return;
    try {
      await session.authorized((t) => session.api.endAllSessions(t));
      session.endHere('sessionsEnded');
    } catch (e) {
      toast.error("Couldn't end your sessions", e);
      reload();
    }
  };

  const sessions = query.data ?? [];
  const paged = usePaged(sessions);
  return (
    <>
      <div className="page-body page-body--after">
        <SectionTitle
          trailing={
            sessions.length > 0 && (
              <button type="button" className="btn btn--text btn--danger-text" onClick={() => void endAll()}>
                End all sessions
              </button>
            )
          }
        >
          Your sessions{query.isSuccess ? ` · ${sessions.length}` : ''}
        </SectionTitle>
      </div>
      <section className="card account sessions" aria-label="Your sessions">
        {query.isError ? (
          <LoadError error={query.error} onRetry={reload} />
        ) : query.isPending ? (
          <Centered>
            <Spinner />
          </Centered>
        ) : sessions.length === 0 ? (
          <EmptyState icon={<MonitorIcon size={24} />} title="No active sessions" />
        ) : (
          <ul className="sessions__list">
            {paged.rows.map((s) => (
              <li key={s.sessionId} className="setting-row sessions__row">
                <IconTile>{s.client === 'mobile' ? <PhoneIcon size={18} /> : <MonitorIcon size={18} />}</IconTile>
                <div className="setting-row__text">
                  <p className="setting-row__label">
                    {clientLabel(s)}
                    {s.deviceId && <span className="sessions__device"> · {s.deviceId}</span>}
                  </p>
                  <p className="setting-row__sub">
                    Signed in {formatDateTime(s.signedInAt)} · last used {formatDateTime(s.lastUsedAt)}
                  </p>
                </div>
                <button
                  type="button"
                  className="btn btn--outline"
                  aria-label={`End session: ${clientLabel(s)}${s.deviceId ? ` on ${s.deviceId}` : ''}, signed in ${formatDateTime(s.signedInAt)}`}
                  onClick={() => void endOne(s)}
                >
                  End session
                </button>
              </li>
            ))}
          </ul>
        )}
        {sessions.length > 0 && <Pagination paged={paged} noun="sessions" />}
      </section>
    </>
  );
}
