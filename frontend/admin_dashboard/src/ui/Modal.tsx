import { useEffect, useId, useRef, type KeyboardEvent, type ReactNode } from 'react';

const focusable =
  'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * A centred dialog over a dimmed page. Escape closes it unless it's busy. While it's open, keyboard focus stays
 * inside (Tab wraps around), and when it closes, focus goes back to whatever opened it.
 */
export function Modal({
  title,
  children,
  actions,
  onClose,
}: {
  title: string;
  children: ReactNode;
  actions: ReactNode;
  onClose: (() => void) | null;
}) {
  const titleId = useId();
  const dialog = useRef<HTMLDivElement>(null);
  // Read during the first render, before a field inside takes focus with autoFocus.
  const opener = useRef(document.activeElement instanceof HTMLElement ? document.activeElement : null);

  useEffect(() => {
    if (!onClose) return;
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  // Focus moves in (unless a field already took it with autoFocus) and returns to the opener afterwards.
  useEffect(() => {
    const box = dialog.current;
    if (box && !box.contains(document.activeElement)) box.querySelector<HTMLElement>(focusable)?.focus();
    const back = opener.current;
    return () => {
      if (back?.isConnected) back.focus();
    };
  }, []);

  const trapTab = (e: KeyboardEvent) => {
    if (e.key !== 'Tab' || !dialog.current) return;
    const items = [...dialog.current.querySelectorAll<HTMLElement>(focusable)];
    if (items.length === 0) return;
    const first = items[0]!;
    const last = items[items.length - 1]!;
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  };

  return (
    <div className="modal-backdrop">
      <div
        ref={dialog}
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={trapTab}
      >
        <h2 id={titleId} className="modal__title">
          {title}
        </h2>
        <div className="modal__content">{children}</div>
        <div className="modal__actions">{actions}</div>
      </div>
    </div>
  );
}
