import { createContext, useContext, useEffect } from 'react';
import { describeError } from '../core/messages';
import { NeedsSignIn } from '../core/session';

export type ToastKind = 'success' | 'error' | 'info';

export interface ToastApi {
  show(toast: { title: string; message?: string; kind?: ToastKind }): void;
  dismiss(): void;
}

export const ToastContext = createContext<ToastApi | null>(null);

export interface Toasts extends ToastApi {
  success(title: string, message?: string): void;
  /** [title] says what failed and the message says why; ended sessions are handled on the sign-in screen. */
  error(title: string, error: unknown): void;
}

export function useToast(): Toasts {
  const api = useContext(ToastContext);
  if (!api) throw new Error('useToast outside ToastProvider');
  return {
    ...api,
    success: (title, message) => api.show({ title, message, kind: 'success' }),
    error: (title, error) => {
      if (error instanceof NeedsSignIn) return;
      api.show({ title, message: describeError(error), kind: 'error' });
    },
  };
}

/** Shows a toast when a list fails to load; the page still shows its own error state. */
export function useLoadFailureToast(title: string, error: unknown): void {
  const { error: showError } = useToast();
  useEffect(() => {
    if (error) showError(title, error);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once per failure, not on every render
  }, [error]);
}
