import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useSession } from '../app/sessionContext';
import { cliniciansKey, meKey } from '../core/queryKeys';
import { NeedsSignIn } from '../core/session';
import { Field, Spinner } from '../ui/components';
import { Modal } from '../ui/Modal';
import { useToast } from '../ui/toastContext';

/** The server's limit (ProfileService.MaxFullNameLength). */
const maxFullNameLength = 120;

/**
 * Changes your own full name (profile management plan, step 4). Afterwards the profile panel, My account and the
 * clinicians list all show the new name without a reload.
 */
export function EditProfileDialog({ fullName, onClose }: { fullName: string; onClose: () => void }) {
  const session = useSession();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [name, setName] = useState(fullName);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (busy) return;
    const trimmed = name.trim();
    if (!trimmed) {
      setError('Enter your full name');
      return;
    }
    setError(undefined);
    setBusy(true);
    toast.dismiss();
    try {
      const profile = await session.authorized((t) => session.api.updateProfile(t, trimmed));
      queryClient.setQueryData(meKey, profile);
      void queryClient.invalidateQueries({ queryKey: cliniciansKey });
      toast.success('Profile updated', `Your name is now ${profile.fullName}.`);
      onClose();
    } catch (err) {
      toast.error("Couldn't update your profile", err);
      setBusy(false);
      if (err instanceof NeedsSignIn) onClose();
    }
  };

  return (
    <Modal
      title="Edit profile"
      onClose={busy ? null : onClose}
      actions={
        <>
          <button type="button" className="btn btn--text" disabled={busy} onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="edit-profile" className="btn btn--primary" disabled={busy}>
            {busy ? <Spinner small /> : 'Save'}
          </button>
        </>
      }
    >
      <form id="edit-profile" className="form" onSubmit={submit} noValidate>
        <Field
          label="Full name"
          error={error}
          help="Other admins see this name in the clinicians list. Your username stays the same."
        >
          <input
            name="fullName"
            autoComplete="name"
            autoFocus
            maxLength={maxFullNameLength}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </Field>
      </form>
    </Modal>
  );
}
