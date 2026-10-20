import { useState, type FormEvent } from 'react';
import { useSession } from '../app/sessionContext';
import { minPasswordLength, type ClinicianSummary } from '../core/models';
import { NeedsSignIn } from '../core/session';
import { Field, Spinner } from '../ui/components';
import { Modal } from '../ui/Modal';
import { useToast } from '../ui/toastContext';

/** Sets a new password for a clinician who forgot theirs. The server also unlocks them and ends their sessions. */
export function ResetPasswordDialog({
  clinician,
  onClose,
  onReset,
}: {
  clinician: ClinicianSummary;
  onClose: () => void;
  onReset: () => void;
}) {
  const session = useSession();
  const toast = useToast();
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    if (password.length < minPasswordLength) {
      setError(`At least ${minPasswordLength} characters`);
      return;
    }
    setError(undefined);
    setBusy(true);
    toast.dismiss();
    try {
      await session.authorized((t) => session.api.resetPassword(t, clinician.username, password));
      onReset();
    } catch (err) {
      toast.error(`Couldn't reset the password for ${clinician.username}`, err);
      setBusy(false);
      if (err instanceof NeedsSignIn) onClose();
    }
  };

  return (
    <Modal
      title={`Reset password for ${clinician.username}`}
      onClose={busy ? null : onClose}
      actions={
        <>
          <button type="button" className="btn btn--text" disabled={busy} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="reset-password" className="btn btn--primary" disabled={busy}>
            {busy ? <Spinner small /> : 'Reset password'}
          </button>
        </>
      }
    >
      <form id="reset-password" className="form" onSubmit={submit} noValidate>
        <p>
          {clinician.fullName} is signed out on every device and signs in again with the new password. Do this only
          after confirming who is asking.
        </p>
        <Field
          label="New password"
          error={error}
          help={`At least ${minPasswordLength} characters. Share it with the clinician in person; they choose their own at their next sign-in.`}
        >
          <input
            name="password"
            type="password"
            autoComplete="new-password"
            autoFocus
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
      </form>
    </Modal>
  );
}
