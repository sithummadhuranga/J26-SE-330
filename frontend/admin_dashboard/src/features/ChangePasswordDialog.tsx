import { useState, type FormEvent } from 'react';
import { useSession } from '../app/sessionContext';
import { ApiError } from '../core/api';
import { describeError } from '../core/messages';
import { minPasswordLength } from '../core/models';
import { NeedsSignIn } from '../core/session';
import { Field, Spinner } from '../ui/components';
import { Modal } from '../ui/Modal';
import { useToast } from '../ui/toastContext';

type Errors = { current?: string; next?: string; again?: string };

/**
 * Changes your own password (profile management plan, step 6). The server then ends every session, including
 * this one, so afterwards the dashboard goes back to the sign-in screen and asks for the new password.
 */
export function ChangePasswordDialog({ onClose }: { onClose: () => void }) {
  const session = useSession();
  const toast = useToast();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [again, setAgain] = useState('');
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Errors>({});

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const found: Errors = {};
    if (!current) found.current = 'Enter your current password';
    if (next.length < minPasswordLength) found.next = `At least ${minPasswordLength} characters`;
    else if (next === current) found.next = 'Choose a password different from your current one';
    if (again !== next) found.again = 'The two new passwords do not match';
    setErrors(found);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    toast.dismiss();
    try {
      await session.authorized((t) => session.api.changePassword(t, current, next));
      session.endHere('passwordChanged'); // the sign-in screen takes over and asks for the new password
    } catch (err) {
      setBusy(false);
      // A wrong current password is shown next to that field. Anything else becomes a toast.
      if (err instanceof ApiError && err.code === 'INVALID_CREDENTIALS') {
        setErrors({ current: describeError(err) });
        setCurrent('');
        return;
      }
      toast.error("Couldn't change your password", err);
      if (err instanceof NeedsSignIn) onClose();
    }
  };

  return (
    <Modal
      title="Change password"
      onClose={busy ? null : onClose}
      actions={
        <>
          <button type="button" className="btn btn--text" disabled={busy} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="change-password" className="btn btn--primary" disabled={busy}>
            {busy ? <Spinner small /> : 'Change password'}
          </button>
        </>
      }
    >
      <form id="change-password" className="form" onSubmit={submit} noValidate>
        <p>You will be signed out everywhere, here included, and sign in again with the new password.</p>
        <Field label="Current password" error={errors.current}>
          <input
            name="currentPassword"
            type="password"
            autoComplete="current-password"
            autoFocus
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
          />
        </Field>
        <Field label="New password" error={errors.next} help={`At least ${minPasswordLength} characters.`}>
          <input
            name="newPassword"
            type="password"
            autoComplete="new-password"
            value={next}
            onChange={(e) => setNext(e.target.value)}
          />
        </Field>
        <Field label="New password again" error={errors.again}>
          <input
            name="newPasswordAgain"
            type="password"
            autoComplete="new-password"
            value={again}
            onChange={(e) => setAgain(e.target.value)}
          />
        </Field>
      </form>
    </Modal>
  );
}
