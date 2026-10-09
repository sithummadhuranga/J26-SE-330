import { useEffect, useId, type ReactNode } from 'react';

/** A centered dialog over a dimmed page; Escape closes it unless it's busy. */
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

  useEffect(() => {
    if (!onClose) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="modal-backdrop">
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <h2 id={titleId} className="modal__title">
          {title}
        </h2>
        <div className="modal__content">{children}</div>
        <div className="modal__actions">{actions}</div>
      </div>
    </div>
  );
}
