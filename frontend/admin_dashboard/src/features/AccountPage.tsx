import { useQuery, useQueryClient } from '@tanstack/react-query';
import QRCode from 'qrcode';
import { useEffect, useState, type FormEvent } from 'react';
import { useAdmin, useSession } from '../app/sessionContext';
import type { MfaEnrollment } from '../core/models';
import { Centered, Field, LoadError, PageHeader, Spinner, StatusChip } from '../ui/components';
import { useLoadFailureToast, useToast } from '../ui/toastContext';

/** The same list as the clinicians page, so turning MFA on here updates the chip there too. */
const cliniciansKey = ['clinicians'];

/** The signed-in admin's own account: turning on two-step sign-in (architecture §7.3). */
export function AccountPage() {
  const session = useSession();
  const admin = useAdmin();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [enrollment, setEnrollment] = useState<MfaEnrollment | null>(null);
  const [busy, setBusy] = useState(false);

  const query = useQuery({
    queryKey: cliniciansKey,
    queryFn: () => session.authorized((t) => session.api.clinicians(t)),
  });
  useLoadFailureToast("Couldn't load your account", query.error);
  const reload = () => void queryClient.invalidateQueries({ queryKey: cliniciansKey });
  const me = query.data?.find((c) => c.username === admin?.username);

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
      <PageHeader title="My account" subtitle={`Signed in as ${admin?.username ?? ''}.`} />
      {query.isError ? (
        <LoadError error={query.error} onRetry={reload} />
      ) : query.isPending ? (
        <Centered>
          <Spinner />
        </Centered>
      ) : (
        <section className="card account" aria-label="Two-step sign-in">
          <h2 className="account__title">
            Two-step sign-in{' '}
            {me?.mfaEnabled ? <StatusChip label="On" tone="blue" /> : <StatusChip label="Off" tone="error" />}
          </h2>
          {me?.mfaEnabled ? (
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
      )}
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
