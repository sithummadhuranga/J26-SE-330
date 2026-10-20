import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import { auditActionLabel, auditReasonLabel } from '../core/messages';
import type { AuthAuditEntry } from '../core/models';
import { usePaged } from '../core/paging';
import { fuzzyMatch } from '../core/search';
import { Centered, EmptyState, LoadError, PageHeader, Spinner, StatusChip } from '../ui/components';
import { HistoryIcon } from '../ui/icons';
import { Pagination } from '../ui/Pagination';
import { useLoadFailureToast } from '../ui/toastContext';

const limits = [100, 250, 500, 1000] as const;

/**
 * MFA_REQUIRED and PASSWORD_CHANGE_REQUIRED are the server asking for the next step, not failed attempts.
 * The auth-health metrics follow the same rule.
 */
const prompts = new Set(['MFA_REQUIRED', 'PASSWORD_CHANGE_REQUIRED']);
const isFailure = (e: AuthAuditEntry) => !e.success && !prompts.has(e.reasonCode ?? '');

function matches(e: AuthAuditEntry, query: string, failuresOnly: boolean): boolean {
  if (failuresOnly && !isFailure(e)) return false;
  const result = e.success ? 'Succeeded success' : `${auditReasonLabel(e.reasonCode)} failed failure`;
  return fuzzyMatch(query, [e.username, auditActionLabel(e.action), result, e.deviceId, e.actorUsername]);
}

/** Recent sign-in and admin events for the facility, filtered in the browser (up to 1000 rows). */
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
  const paged = usePaged(rows);
  // A new search, filter or amount starts again at the newest events.
  const fresh =
    <T,>(set: (value: T) => void) =>
    (value: T) => {
      set(value);
      paged.setPage(0);
    };

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
              onChange={(e) => fresh(setSearch)(e.target.value)}
            />
          </div>
          <div className="segmented" role="group" aria-label="Which events">
            <button type="button" aria-pressed={!failuresOnly} onClick={() => fresh(setFailuresOnly)(false)}>
              All events
            </button>
            <button type="button" aria-pressed={failuresOnly} onClick={() => fresh(setFailuresOnly)(true)}>
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
            <select aria-label="How many events" value={limit} onChange={(e) => fresh(setLimit)(Number(e.target.value))}>
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
          <EmptyState
            icon={<HistoryIcon size={24} />}
            title="No matching events"
            message="Try a different search, or show all events."
          />
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
                {paged.rows.map((e) => (
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
        {query.isSuccess && rows.length > 0 && <Pagination paged={paged} noun="events" />}
      </section>
    </>
  );
}
