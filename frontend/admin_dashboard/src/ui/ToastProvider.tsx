import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react';
import { ToastContext, type ToastApi, type ToastKind } from './toastContext';

interface Toast {
  id: number;
  title: string;
  message?: string;
  kind: ToastKind;
}

/** A dismissible top-right toast shown above dialogs, one at a time; errors stay longer than confirmations. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toast, setToast] = useState<Toast | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const nextId = useRef(0);

  const dismiss = useCallback(() => {
    clearTimeout(timer.current);
    setToast(null);
  }, []);

  const show = useCallback<ToastApi['show']>(({ title, message, kind = 'info' }) => {
    clearTimeout(timer.current);
    setToast({ id: ++nextId.current, title, message, kind });
    timer.current = setTimeout(() => setToast(null), kind === 'error' ? 7000 : 4000);
  }, []);

  const api = useMemo(() => ({ show, dismiss }), [show, dismiss]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      {toast && (
        <div
          key={toast.id}
          className={`toast toast--${toast.kind}`}
          role={toast.kind === 'error' ? 'alert' : 'status'}
          data-testid="toast"
        >
          <div className="toast__body">
            <strong className="toast__title">{toast.title}</strong>
            {toast.message && <p className="toast__message">{toast.message}</p>}
          </div>
          <button type="button" className="btn btn--text" onClick={dismiss}>
            Dismiss
          </button>
        </div>
      )}
    </ToastContext.Provider>
  );
}
