import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import { auditActionLabel, auditReasonLabel } from '../core/messages';
import type { AuthAuditEntry } from '../core/models';
import { fuzzyMatch } from '../core/search';
import { Centered, LoadError, PageHeader, Spinner, StatusChip } from '../ui/components';
import { useLoadFailureToast } from '../ui/toastContext';

const limits = [100, 250, 500, 1000] as const;

/** MFA_REQUIRED is the server asking for a code, not a failed attempt (same rule as the auth-health metrics). */
const isFailure = (e: AuthAuditEntry) => !e.success && e.reasonCode !== 'MFA_REQUIRED';

function matches(e: AuthAuditEntry, query: string, failuresOnly: boolean): boolean {
  if (failuresOnly && !isFailure(e)) return false;
  const result = e.success ? 'Succeeded success' : `${auditReasonLabel(e.reasonCode)} failed failure`;
  return fuzzyMatch(query, [e.username, auditActionLabel(e.action), result, e.deviceId, e.actorUsername]);
}

/** Recent sign-in and admin events for the facility, filtered in the browser (max 1000 rows). */
export function AuditPage() {
  const session = useSession();
  const queryClient = useQueryClient();
  const [limit, setLimit] = useState<number>(limits[0]);
  const [search, setSearch] = useState('');
  const [failuresOnly, setFailuresOnly] = useState(false);

  const query = useQuery({
    queryKey: ['audit', limit],
    queryFn: () => session.authorized((t) => session.api.authAudit(t, limit)),
  });
  useLoadFailureToast("Couldn't load the audit log", query.error);
  const reload = () => void queryClient.invalidateQueries({ queryKey: ['audit'] });

  const all = query.data ?? [];
  const rows = all.filter((e) => matches(e, search.trim(), failuresOnly));

  return (
    <>
      <PageHeader
        title="Audit log"
        subtitle="Sign-ins, lockouts and admin actions at your facility, newest first."
        actions={
          <button type="button" className="btn btn--outline" onClick={reload}>
            Refresh
          </button>
        }
      />
      <section className="card panel">
        <div className="toolbar">
          <div className="search">
            <svg className="search__icon" viewBox="0 0 16 16" aria-hidden="true">
              <circle cx="7" cy="7" r="5" />
              <path d="M11 11l3.5 3.5" />
            </svg>
            <input
              type="search"
              className="search__input"
              aria-label="Search audit log"
              placeholder="Search user, action, result or device"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>
          <div className="segmented" role="group" aria-label="Which events">
            <button type="button" aria-pressed={!failuresOnly} onClick={() => setFailuresOnly(false)}>
              All events
            </button>
            <button type="button" aria-pressed={failuresOnly} onClick={() => setFailuresOnly(true)}>
              Failures only
            </button>
          </div>
          <span className="toolbar__spacer" />
          {query.isSuccess && (
            <span className="toolbar__count" aria-live="polite">
              {rows.length === all.length ? `${all.length} events` : `${rows.length} of ${all.length} events`}
            </span>
          )}
          <label className="toolbar__select">
            <span>Show</span>
            <select aria-label="How many events" value={limit} onChange={(e) => setLimit(Number(e.target.value))}>
              {limits.map((l) => (
                <option key={l} value={l}>
                  Last {l}
                </option>
              ))}
            </select>
          </label>
        </div>

        {query.isError ? (
          <LoadError error={query.error} onRetry={reload} />
        ) : query.isPending ? (
          <Centered>
            <Spinner />
          </Centered>
        ) : rows.length === 0 ? (
          <Centered>No matching events.</Centered>
        ) : (
          <div className="panel__scroll">
            <table className="table table--fixed">
              <colgroup>
                <col style={{ width: '15%' }} />
                <col style={{ width: '18%' }} />
                <col style={{ width: '16%' }} />
                <col style={{ width: '18%' }} />
                <col style={{ width: '15%' }} />
                <col style={{ width: '18%' }} />
              </colgroup>
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Action</th>
                  <th>Result</th>
                  <th>User</th>
                  <th>By admin</th>
                  <th>Device</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((e) => (
                  <tr key={e.id}>
                    <td className="table__time">{formatDateTime(e.recordedAt)}</td>
                    <td>{auditActionLabel(e.action)}</td>
                    <td>
                      {e.success ? (
                        <StatusChip label="Succeeded" tone="teal" />
                      ) : (
                        <StatusChip label={auditReasonLabel(e.reasonCode)} tone={isFailure(e) ? 'error' : 'blue'} />
                      )}
                    </td>
                    <td>{e.username || '—'}</td>
                    <td>{e.actorUsername ?? '—'}</td>
                    <td className={e.deviceId ? 'table__mono' : undefined}>{e.deviceId ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  );
}
