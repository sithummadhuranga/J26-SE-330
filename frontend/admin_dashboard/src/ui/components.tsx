import { cloneElement, useEffect, useId, useRef, useState, type CSSProperties, type ReactElement, type ReactNode } from 'react';
import { initialsOf } from '../core/format';
import { describeError } from '../core/messages';

/** Page heading with a short explanation and actions on the right. */
export function PageHeader({ title, subtitle, actions }: { title: string; subtitle: string; actions?: ReactNode }) {
  return (
    <header className="page-header">
      <div>
        <h1 className="page-header__title">{title}</h1>
        <p className="page-header__subtitle">{subtitle}</p>
      </div>
      <div className="page-header__actions">{actions}</div>
    </header>
  );
}

export type ChipTone = 'teal' | 'blue' | 'error' | 'muted';

/** A small colored label (Active, Locked, MFA on, ...). */
export function StatusChip({ label, tone, title }: { label: string; tone: ChipTone; title?: string }) {
  return (
    <span className={`chip chip--${tone}`} title={title}>
      {label}
    </span>
  );
}

export function Spinner({ small = false }: { small?: boolean }) {
  return <span className={`spinner${small ? ' spinner--small' : ''}`} role="progressbar" aria-label="Loading" />;
}

export function Centered({ children }: { children: ReactNode }) {
  return <div className="centered">{children}</div>;
}

// ---- building blocks shared with the mobile app's look (mobile/lib/core/widgets.dart) ----

/** A round soft-teal avatar with initials. Decorative: the name is always written next to it. */
export function InitialsAvatar({ name, size = 36 }: { name: string; size?: number }) {
  return (
    <span className="avatar" style={{ width: size, height: size, fontSize: size * 0.33 }} aria-hidden="true">
      {initialsOf(name)}
    </span>
  );
}

/** A rounded square holding an icon, like the app's setting rows and empty states. */
export function IconTile({ children, tone = 'accent' }: { children: ReactNode; tone?: 'accent' | 'neutral' }) {
  return (
    <span className={`icon-tile icon-tile--${tone}`} aria-hidden="true">
      {children}
    </span>
  );
}

/** Nothing to show yet: an icon tile, a bold line and a hint (the app's EmptyState). */
export function EmptyState({ icon, title, message }: { icon: ReactNode; title: string; message?: string }) {
  return (
    <div className="empty">
      <IconTile tone="neutral">{icon}</IconTile>
      <p className="empty__title">{title}</p>
      {message && <p className="empty__message">{message}</p>}
    </div>
  );
}

/** A small bold heading above a group, with an optional count or action on the right (the app's SectionTitle). */
export function SectionTitle({ children, trailing }: { children: ReactNode; trailing?: ReactNode }) {
  return (
    <div className="section-title">
      <h2 className="section-title__text">{children}</h2>
      {trailing}
    </div>
  );
}

/** A quiet grey strip with an icon, for a rule worth knowing (the app's NoticeStrip). */
export function NoticeStrip({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <p className="notice">
      <span className="notice__icon" aria-hidden="true">
        {icon}
      </span>
      <span>{children}</span>
    </p>
  );
}

/**
 * The main action of a page as a teal gradient card (the app's "New Assessment" card). [label] is the button's
 * accessible name, so it reads the same as a plain button would.
 */
export function HeroAction({
  label,
  description,
  icon,
  onClick,
}: {
  label: string;
  description: string;
  icon: ReactNode;
  onClick: () => void;
}) {
  return (
    <button type="button" className="hero" aria-label={label} onClick={onClick}>
      <span className="hero__icon" aria-hidden="true">
        {icon}
      </span>
      <span className="hero__text" aria-hidden="true">
        <span className="hero__title">{label}</span>
        <span className="hero__sub">{description}</span>
      </span>
      <svg className="hero__chevron" width="22" height="22" viewBox="0 0 24 24" aria-hidden="true">
        <path d="M9.5 6l6 6-6 6" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    </button>
  );
}

/** A list that failed to load, with a way to try again. */
export function LoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  return (
    <Centered>
      <p className="load-error">{describeError(error)}</p>
      <button type="button" className="btn btn--outline" onClick={onRetry}>
        Try again
      </button>
    </Centered>
  );
}

/**
 * A table inside a card that scrolls sideways only when it needs more room than it has. [footer] (the pager) stays
 * at the bottom of the card and does not scroll with the table.
 */
export function TableCard({ children, footer }: { children: ReactNode; footer?: ReactNode }) {
  return (
    <div className="card table-card">
      <div className="table-card__scroll">
        <table className="table">{children}</table>
      </div>
      {footer}
    </div>
  );
}

const menuItemHeight = 40;

/** An "Actions" menu that floats above the page, opens upwards near the bottom, and closes on any outside action. */
export function ActionsMenu({
  label,
  items,
}: {
  label: string;
  items: { label: string; danger?: boolean; onSelect: () => void }[];
}) {
  const [position, setPosition] = useState<CSSProperties | null>(null);
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const open = position !== null;

  const toggle = () => {
    if (open || !button.current) {
      setPosition(null);
      return;
    }
    const rect = button.current.getBoundingClientRect();
    const height = items.length * menuItemHeight + 10;
    const right = window.innerWidth - rect.right;
    setPosition(
      window.innerHeight - rect.bottom < height + 8 && rect.top > height + 8
        ? { right, bottom: window.innerHeight - rect.top + 4 }
        : { right, top: rect.bottom + 4 },
    );
  };

  useEffect(() => {
    if (!open) return;
    const close = () => setPosition(null);
    const onPointer = (e: PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    document.addEventListener('pointerdown', onPointer);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [open]);

  return (
    <div className="menu" ref={root}>
      <button
        ref={button}
        type="button"
        className="btn btn--text"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={label}
        onClick={toggle}
      >
        Actions
      </button>
      {open && (
        <ul className="menu__list" role="menu" style={position}>
          {items.map((item) => (
            <li key={item.label} role="none">
              <button
                type="button"
                role="menuitem"
                className={`menu__item${item.danger ? ' menu__item--danger' : ''}`}
                onClick={() => {
                  setPosition(null);
                  item.onSelect();
                }}
              >
                {item.label}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** A labelled input with an optional help line and validation message. */
export function Field({
  label,
  error,
  help,
  children,
}: {
  label: string;
  error?: string;
  help?: string;
  children: ReactElement<{ id?: string; 'aria-describedby'?: string; 'aria-invalid'?: boolean }>;
}) {
  const id = useId();
  const note = error ?? help;
  return (
    <div className={`field${error ? ' field--invalid' : ''}`}>
      <label className="field__label" htmlFor={id}>
        {label}
      </label>
      {cloneElement(children, { id, 'aria-describedby': note ? `${id}-note` : undefined, 'aria-invalid': !!error })}
      {note && (
        <span id={`${id}-note`} className={error ? 'field__error' : 'field__help'}>
          {note}
        </span>
      )}
    </div>
  );
}
