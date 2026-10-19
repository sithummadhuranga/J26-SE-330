import { useQuery, useQueryClient } from '@tanstack/react-query';
import QRCode from 'qrcode';
import { useEffect, useState, type FormEvent } from 'react';
import { useLocation } from 'react-router';
import { useAdmin, useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import { roleLabel, type MfaEnrollment } from '../core/models';
import { cliniciansKey, meKey } from '../core/queryKeys';
import {
  Centered,
  Field,
  IconTile,
  InitialsAvatar,
  LoadError,
  PageHeader,
  SectionTitle,
  Spinner,
  StatusChip,
} from '../ui/components';
import { EditIcon, KeyIcon, ShieldIcon } from '../ui/icons';
import { useLoadFailureToast, useToast } from '../ui/toastContext';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { EditProfileDialog } from './EditProfileDialog';
import { SessionsSection } from './SessionsSection';

/** Scrolls to and focuses the section in the URL hash (#profile, #security, #sessions), as the profile panel links. */
function useSectionFromHash() {
  const { hash } = useLocation();
  useEffect(() => {
    if (!hash) return;
    const section = document.getElementById(hash.slice(1));
    section?.scrollIntoView?.({ block: 'start', behavior: 'smooth' });
    section?.focus({ preventScroll: true });
  }, [hash]);
}

/**
 * Your own account (plan: profile management, step 9): Profile (details and Edit profile), Security (password and
 * two-step sign-in) and Your sessions.
 */
export function AccountPage() {
  const session = useSession();
  const admin = useAdmin();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [enrollment, setEnrollment] = useState<MfaEnrollment | null>(null);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const [changingPassword, setChangingPassword] = useState(false);
  useSectionFromHash();

  const query = useQuery({ queryKey: meKey, queryFn: () => session.authorized((t) => session.api.me(t)) });
  useLoadFailureToast("Couldn't load your account", query.error);
  const reload = () => {
    void queryClient.invalidateQueries({ queryKey: meKey });
    void queryClient.invalidateQueries({ queryKey: cliniciansKey }); // the clinicians list shows "MFA on" too
  };
  const me = query.data;

  const start = async () => {
    if (busy) return;
    setBusy(true);
    toast.dismiss();
    try {
      setEnrollment(await session.authorized((t) => session.api.mfaEnroll(t)));
    } catch (e) {
      toast.error("Couldn't start two-step setup", e);
      reload();
    }
    setBusy(false);
  };

  const onConfirmed = () => {
    setEnrollment(null);
    toast.success('Two-step sign-in is on', 'Next time you sign in, you will be asked for a code from your app.');
    reload();
  };

  return (
    <>
      <PageHeader title="My account" subtitle="Your profile, how you sign in, and where you are signed in." />
      <div className="page-body">
        <div className="profile">
          <InitialsAvatar name={admin?.username ?? '?'} size={54} />
          <div className="profile__who">
            <p className="profile__name">{me?.fullName ?? admin?.username}</p>
            <p className="profile__sub">
              {admin?.username} · {roleLabel(admin?.role ?? 'admin')} · Facility {admin?.facilityId}
            </p>
          </div>
        </div>
      </div>

      {query.isError ? (
        <LoadError error={query.error} onRetry={reload} />
      ) : query.isPending || !me ? (
        <Centered>
          <Spinner />
        </Centered>
      ) : (
        <>
          <div id="profile" className="page-body page-body--section" tabIndex={-1}>
            <SectionTitle>Profile</SectionTitle>
          </div>
          <section className="card account" aria-label="Profile">
            <dl className="facts">
              <div>
                <dt>Full name</dt>
                <dd>{me.fullName}</dd>
              </div>
              <div>
                <dt>Username</dt>
                <dd>{me.username}</dd>
              </div>
              <div>
                <dt>Role</dt>
                <dd>{roleLabel(me.role)}</dd>
              </div>
              <div>
                <dt>Facility</dt>
                <dd>{me.facilityId}</dd>
              </div>
              <div>
                <dt>Account created</dt>
                <dd>{formatDateTime(me.createdAt)}</dd>
              </div>
              <div>
                <dt>Last sign-in</dt>
                <dd>{formatDateTime(me.lastSignInAt)}</dd>
              </div>
            </dl>
            <div className="account__actions">
              <button type="button" className="btn btn--outline" onClick={() => setEditing(true)}>
                <EditIcon size={16} />
                Edit profile
              </button>
            </div>
          </section>

          <div id="security" className="page-body page-body--section page-body--after" tabIndex={-1}>
            <SectionTitle>Security</SectionTitle>
          </div>
          <section className="card account" aria-label="Password">
            <div className="setting-row">
              <IconTile>
                <KeyIcon size={20} />
              </IconTile>
              <div className="setting-row__text">
                <h2 className="setting-row__label">Password</h2>
                <p className="setting-row__sub">Changing it signs you out everywhere, here included</p>
              </div>
              <button type="button" className="btn btn--outline" onClick={() => setChangingPassword(true)}>
                Change password
              </button>
            </div>
          </section>
          <section className="card account" aria-label="Two-step sign-in">
            <div className="setting-row">
              <IconTile>
                <ShieldIcon size={20} />
              </IconTile>
              <div className="setting-row__text">
                <h2 className="setting-row__label">Two-step sign-in</h2>
                <p className="setting-row__sub">A code from your phone as well as your password</p>
              </div>
              {me.mfaEnabled ? <StatusChip label="On" tone="blue" /> : <StatusChip label="Off" tone="error" />}
            </div>
            {me.mfaEnabled ? (
              <p>
                Each sign-in asks for a code from your authenticator app. If you lose your phone, ask another admin to
                reset two-step sign-in for you, then set it up again.
              </p>
            ) : enrollment ? (
              <ConfirmStep enrollment={enrollment} onConfirmed={onConfirmed} onCancel={() => setEnrollment(null)} />
            ) : (
              <>
                <p>
                  Admin accounts can change who has access to patient data, so protect yours with a code from an
                  authenticator app (Google Authenticator, Microsoft Authenticator or similar) as well as your password.
                </p>
                <div>
                  <button type="button" className="btn btn--primary" disabled={busy} onClick={() => void start()}>
                    {busy ? <Spinner small /> : 'Set up two-step sign-in'}
                  </button>
                </div>
              </>
            )}
          </section>

          <SessionsSection />
        </>
      )}
      {changingPassword && <ChangePasswordDialog onClose={() => setChangingPassword(false)} />}
      {editing && me && <EditProfileDialog fullName={me.fullName} onClose={() => setEditing(false)} />}
    </>
  );
}

/** Shows the QR code and secret, then turns MFA on once the admin enters a code their app made from it. */
function ConfirmStep({
  enrollment,
  onConfirmed,
  onCancel,
}: {
  enrollment: MfaEnrollment;
  onConfirmed: () => void;
  onCancel: () => void;
}) {
  const session = useSession();
  const toast = useToast();
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  // SVG needs no canvas, and the secret stays in this page: it is never sent to a QR service.
  useEffect(() => {
    let live = true;
    void QRCode.toString(enrollment.otpauthUri, { type: 'svg', margin: 1 }).then((svg) => {
      if (live) setQr(`data:image/svg+xml;utf8,${encodeURIComponent(svg)}`);
    });
    return () => {
      live = false;
    };
  }, [enrollment.otpauthUri]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const digits = code.replace(/\s/g, '');
    if (!/^\d{6}$/.test(digits)) {
      setError('Enter the 6-digit code from your app');
      return;
    }
    setError(undefined);
    setBusy(true);
    toast.dismiss();
    try {
      await session.authorized((t) => session.api.confirmMfa(t, digits));
      onConfirmed();
    } catch (err) {
      toast.error("Couldn't turn on two-step sign-in", err);
      setBusy(false);
    }
  };

  return (
    <form className="form" onSubmit={submit} noValidate>
      <p>1. Scan this code with your authenticator app.</p>
      <div className="account__qr">{qr ? <img src={qr} alt="QR code for your authenticator app" /> : <Spinner />}</div>
      <p className="account__secret">
        Can't scan it? Enter this key instead: <code data-testid="mfa-secret">{enrollment.secret}</code>
      </p>
      <p>2. Enter the 6-digit code the app shows.</p>
      <Field label="Code from your app" error={error}>
        <input
          name="code"
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={7}
          value={code}
          onChange={(e) => setCode(e.target.value)}
        />
      </Field>
      <div className="account__actions">
        <button type="button" className="btn btn--text" disabled={busy} onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="btn btn--primary" disabled={busy}>
          {busy ? <Spinner small /> : 'Turn on'}
        </button>
      </div>
    </form>
  );
}
