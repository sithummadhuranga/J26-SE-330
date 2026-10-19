import { useQuery } from '@tanstack/react-query';
import { useEffect, useId, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { useAdmin, useSession } from '../app/sessionContext';
import { formatDateTime } from '../core/format';
import { roleLabel } from '../core/models';
import { meKey } from '../core/queryKeys';
import { IconTile, InitialsAvatar, Spinner, StatusChip } from '../ui/components';
import { ChevronRightIcon, EditIcon, KeyIcon, MonitorIcon, PersonIcon, ShieldIcon } from '../ui/icons';
import { ChangePasswordDialog } from './ChangePasswordDialog';
import { EditProfileDialog } from './EditProfileDialog';

/**
 * The avatar in the header and the small profile panel it opens (plan: profile management, step 2): who you are,
 * your two-step status, editing your name and password, links to My account, and sign-out.
 */
export function ProfileMenu() {
  const session = useSession();
  const admin = useAdmin();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState(false);
  const [changingPassword, setChangingPassword] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const panelId = useId();

  const me = useQuery({
    queryKey: meKey,
    queryFn: () => session.authorized((t) => session.api.me(t)),
    enabled: open,
  });

  // Closes on Escape (focus back on the avatar) or a click outside, which includes the navigation tabs.
  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      setOpen(false);
      button.current?.focus();
    };
    document.addEventListener('pointerdown', onPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // Keyboard users land in the panel when it opens.
  useEffect(() => {
    if (open) panel.current?.focus();
  }, [open]);

  /** After a dialog opened from the panel closes, focus goes back to the avatar. */
  const backToAvatar = (close: () => void) => () => {
    close();
    button.current?.focus();
  };

  const go = (path: string) => {
    setOpen(false);
    navigate(path);
  };

  const profile = me.data;
  const name = profile?.fullName ?? admin?.username ?? '';

  return (
    <div
      className="profile-menu"
      ref={root}
      onBlur={(e) => {
        // Tabbing out of the panel closes it, like a click outside.
        if (open && e.relatedTarget && !root.current?.contains(e.relatedTarget as Node)) setOpen(false);
      }}
    >
      <button
        ref={button}
        type="button"
        className="profile-menu__trigger"
        aria-label="Your profile"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => setOpen((o) => !o)}
      >
        <InitialsAvatar name={admin?.username ?? '?'} size={40} />
      </button>

      {/* Dims the page behind the bottom sheet on a phone-sized screen; hidden on wider ones. */}
      {open && <div className="profile-backdrop" aria-hidden="true" onClick={() => setOpen(false)} />}
      {open && (
        <div id={panelId} ref={panel} className="profile-panel" role="dialog" aria-label="Your profile" tabIndex={-1}>
          {me.isPending ? (
            <div className="profile-panel__loading">
              <Spinner />
            </div>
          ) : me.isError || !profile ? (
            <p className="profile-panel__error">Couldn't load your profile. Close this and try again.</p>
          ) : (
            <>
              <div className="profile-panel__head">
                <InitialsAvatar name={profile.username} size={52} />
                <div className="profile-panel__who">
                  <p className="profile-panel__name">{name}</p>
                  <p className="profile-panel__username">{profile.username}</p>
                  <p className="profile-panel__sub">
                    {roleLabel(profile.role)} · Facility {profile.facilityId}
                  </p>
                </div>
              </div>
              <dl className="profile-panel__facts">
                <div>
                  <dt>Two-step sign-in</dt>
                  <dd>
                    {profile.mfaEnabled ? <StatusChip label="On" tone="blue" /> : <StatusChip label="Off" tone="error" />}
                  </dd>
                </div>
                <div>
                  <dt>Last sign-in</dt>
                  <dd>{formatDateTime(profile.lastSignInAt)}</dd>
                </div>
                <div>
                  <dt>Account created</dt>
                  <dd>{formatDateTime(profile.createdAt)}</dd>
                </div>
              </dl>
            </>
          )}
          <div className="profile-panel__rows">
            {profile && (
              <button
                type="button"
                className="profile-panel__row"
                onClick={() => {
                  setOpen(false);
                  setEditing(true);
                }}
              >
                <IconTile>
                  <EditIcon size={18} />
                </IconTile>
                <span className="profile-panel__label">Edit profile</span>
                <ChevronRightIcon size={18} className="profile-panel__chevron" />
              </button>
            )}
            <button
              type="button"
              className="profile-panel__row"
              onClick={() => {
                setOpen(false);
                setChangingPassword(true);
              }}
            >
              <IconTile>
                <KeyIcon size={18} />
              </IconTile>
              <span className="profile-panel__label">Change password</span>
              <ChevronRightIcon size={18} className="profile-panel__chevron" />
            </button>
            <button type="button" className="profile-panel__row" onClick={() => go('/account')}>
              <IconTile>
                <PersonIcon size={18} />
              </IconTile>
              <span className="profile-panel__label">My account</span>
              <ChevronRightIcon size={18} className="profile-panel__chevron" />
            </button>
            <button type="button" className="profile-panel__row" onClick={() => go('/account#security')}>
              <IconTile>
                <ShieldIcon size={18} />
              </IconTile>
              <span className="profile-panel__label">Two-step sign-in</span>
              <ChevronRightIcon size={18} className="profile-panel__chevron" />
            </button>
            <button type="button" className="profile-panel__row" onClick={() => go('/account#sessions')}>
              <IconTile>
                <MonitorIcon size={18} />
              </IconTile>
              <span className="profile-panel__label">Your sessions</span>
              <ChevronRightIcon size={18} className="profile-panel__chevron" />
            </button>
          </div>
          <button type="button" className="btn btn--outline btn--block" onClick={() => void session.signOut()}>
            Sign out
          </button>
        </div>
      )}
      {editing && profile && <EditProfileDialog fullName={profile.fullName} onClose={backToAvatar(() => setEditing(false))} />}
      {changingPassword && <ChangePasswordDialog onClose={backToAvatar(() => setChangingPassword(false))} />}
    </div>
  );
}
