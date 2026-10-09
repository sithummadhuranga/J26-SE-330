import { useState, type FormEvent } from 'react';
import { useSession } from '../app/sessionContext';
import { clinicianRoles, minPasswordLength, roleLabel, usernamePattern, type ClinicianRole } from '../core/models';
import { NeedsSignIn } from '../core/session';
import { Field, Spinner } from '../ui/components';
import { Modal } from '../ui/Modal';
import { useToast } from '../ui/toastContext';

/** Register-clinician dialog that checks the server's rules up front and reports the new username. */
export function RegisterClinicianDialog({
  onClose,
  onRegistered,
}: {
  onClose: () => void;
  onRegistered: (username: string) => void;
}) {
  const session = useSession();
  const toast = useToast();
  const [fullName, setFullName] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<ClinicianRole>(clinicianRoles[0]);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<{ fullName?: string; username?: string; password?: string }>({});

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const name = username.trim().toLowerCase();
    const next: typeof errors = {};
    if (!fullName.trim()) next.fullName = 'Enter the full name';
    if (!usernamePattern.test(name)) next.username = 'Use 3–64 of a–z, 0–9, . _ -';
    if (password.length < minPasswordLength) next.password = `At least ${minPasswordLength} characters`;
    setErrors(next);
    if (Object.keys(next).length > 0) return;

    setBusy(true);
    toast.dismiss();
    try {
      await session.authorized((t) =>
        session.api.registerClinician(t, { username: name, password, fullName: fullName.trim(), role }),
      );
      onRegistered(name);
    } catch (err) {
      // The form stays open with what was typed; the toast shows above the dialog.
      toast.error(`Couldn't register ${name}`, err);
      setBusy(false);
      if (err instanceof NeedsSignIn) onClose();
    }
  };

  return (
    <Modal
      title="Register clinician"
      onClose={busy ? null : onClose}
      actions={
        <>
          <button type="button" className="btn btn--text" disabled={busy} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="register-clinician" className="btn btn--primary" disabled={busy}>
            {busy ? <Spinner small /> : 'Register'}
          </button>
        </>
      }
    >
      <form id="register-clinician" className="form" onSubmit={submit} noValidate>
        <Field label="Full name" error={errors.fullName}>
          <input name="fullName" autoFocus value={fullName} onChange={(e) => setFullName(e.target.value)} />
        </Field>
        <Field label="Username" error={errors.username} help="e.g. n.silva — lowercase letters, digits, . _ -">
          <input name="username" autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />
        </Field>
        <Field
          label="Initial password"
          error={errors.password}
          help={`At least ${minPasswordLength} characters. Share it with the clinician in person.`}
        >
          <input
            name="password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        <Field label="Role">
          <select name="role" value={role} onChange={(e) => setRole(e.target.value as ClinicianRole)}>
            {clinicianRoles.map((r) => (
              <option key={r} value={r}>
                {roleLabel(r)}
              </option>
            ))}
          </select>
        </Field>
      </form>
    </Modal>
  );
}
