import { useEffect, useState } from 'react';
import { useSession } from '../app/sessionContext';
import { describeError } from '../core/messages';
import { browser } from '../core/navigation';
import { Spinner } from '../ui/components';
import { useToast } from '../ui/toastContext';

/**
 * Sends the admin to the identity service's sign-in page (ADR 0006). The password, the authenticator code and
 * messages like a locked account are all handled there; this app never sees the password.
 */
export function LoginScreen() {
  const session = useSession();
  const toast = useToast();
  const [busy, setBusy] = useState(false);

  // Back here because the server ended the session, or the last sign-in did not complete: say so once.
  useEffect(() => {
    const failure = session.takeSignInFailure();
    // toast.error stays quiet for NeedsSignIn, but here the admin should know why they're back on this screen.
    if (failure) toast.show({ title: "Couldn't sign in", message: describeError(failure), kind: 'error' });
    else if (session.takeSessionEndedNotice()) {
      toast.show({ title: 'Signed out', message: 'Your session has ended. Please sign in again.' });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const signIn = async () => {
    if (busy) return;
    setBusy(true);
    toast.dismiss();
    try {
      browser.go(await session.beginSignIn());
    } catch (err) {
      toast.error("Couldn't sign in", err);
      setBusy(false);
    }
  };

  return (
    <main className="login">
      <div className="card login__card">
        <p className="login__brand">MELANIN WOUND CDSS</p>
        <h1 className="login__title">Admin dashboard</h1>
        <p className="login__subtitle">Sign in with your facility admin account</p>

        <button type="button" className="btn btn--primary btn--block" disabled={busy} onClick={() => void signIn()}>
          {busy ? <Spinner small /> : 'Sign in'}
        </button>
        <p className="login__note">Nurses and wound specialists sign in on the mobile app.</p>
      </div>
    </main>
  );
}
