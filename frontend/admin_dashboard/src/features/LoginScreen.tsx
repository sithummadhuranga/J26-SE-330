import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useSession } from '../app/sessionContext';
import { ApiError } from '../core/api';
import { Field, Spinner } from '../ui/components';
import { useToast } from '../ui/toastContext';

/** Admin sign-in; shows the code field when the server asks for MFA. */
export function LoginScreen() {
  const session = useSession();
  const toast = useToast();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [totp, setTotp] = useState('');
  const [needsCode, setNeedsCode] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<{ username?: string; password?: string; totp?: string }>({});
  const totpInput = useRef<HTMLInputElement>(null);

  // Back here because the server ended the session (not a sign-out): say so once.
  useEffect(() => {
    if (session.takeSessionEndedNotice()) {
      toast.show({ title: 'Signed out', message: 'Your session has ended. Please sign in again.' });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (needsCode) totpInput.current?.focus();
  }, [needsCode]);

  const validate = () => {
    const next: typeof errors = {};
    if (!username.trim()) next.username = 'Enter your username';
    if (!password) next.password = 'Enter your password';
    if (needsCode && !/^\d{6}$/.test(totp.trim())) next.totp = 'Enter the 6-digit code';
    setErrors(next);
    return Object.keys(next).length === 0;
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy || !validate()) return;
    setBusy(true);
    toast.dismiss();
    try {
      // Success notifies the session's listeners, which swap this screen for the dashboard.
      await session.signIn(username.trim().toLowerCase(), password, needsCode ? totp.trim() : undefined);
    } catch (err) {
      const askForCode = err instanceof ApiError && err.code === 'MFA_REQUIRED';
      // Being asked for a code the first time is the next step, not an error: the code field appears.
      if (!askForCode || needsCode) toast.error("Couldn't sign in", err);
      if (askForCode) setNeedsCode(true);
      if (err instanceof ApiError && err.code === 'INVALID_TOTP') setTotp('');
      setBusy(false);
    }
  };

  const startOver = () => {
    setNeedsCode(false);
    setTotp('');
    setPassword('');
    setErrors({});
  };

  return (
    <main className="login">
      <form className="card login__card" onSubmit={submit} noValidate>
        <p className="login__brand">MELANIN WOUND CDSS</p>
        <h1 className="login__title">Admin dashboard</h1>
        <p className="login__subtitle">Sign in with your facility admin account</p>

        <Field label="Username" error={errors.username}>
          <input
            name="username"
            autoComplete="username"
            autoFocus
            disabled={needsCode}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
        </Field>
        <Field label="Password" error={errors.password}>
          <input
            name="password"
            type="password"
            autoComplete="current-password"
            disabled={needsCode}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        {needsCode && (
          <Field label="Authenticator code" error={errors.totp} help="The 6-digit code from your authenticator app">
            <input
              ref={totpInput}
              name="totp"
              inputMode="numeric"
              autoComplete="one-time-code"
              value={totp}
              onChange={(e) => setTotp(e.target.value)}
            />
          </Field>
        )}

        <button type="submit" className="btn btn--primary btn--block" disabled={busy}>
          {busy ? <Spinner small /> : needsCode ? 'Verify and sign in' : 'Sign in'}
        </button>
        {needsCode && (
          <button type="button" className="btn btn--text btn--block" disabled={busy} onClick={startOver}>
            Use a different account
          </button>
        )}
        <p className="login__note">Nurses and wound specialists sign in on the mobile app.</p>
      </form>
    </main>
  );
}
