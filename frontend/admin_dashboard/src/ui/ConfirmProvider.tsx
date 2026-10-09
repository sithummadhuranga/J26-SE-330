import { useCallback, useRef, useState, type ReactNode } from 'react';
import { ConfirmContext, type ConfirmOptions } from './confirmContext';
import { Modal } from './Modal';

export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [options, setOptions] = useState<ConfirmOptions | null>(null);
  const resolver = useRef<((yes: boolean) => void) | null>(null);

  const confirm = useCallback(
    (next: ConfirmOptions) =>
      new Promise<boolean>((resolve) => {
        resolver.current?.(false);
        resolver.current = resolve;
        setOptions(next);
      }),
    [],
  );

  const answer = (yes: boolean) => {
    resolver.current?.(yes);
    resolver.current = null;
    setOptions(null);
  };

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      {options && (
        <Modal
          title={options.title}
          onClose={() => answer(false)}
          actions={
            <>
              <button type="button" className="btn btn--text" onClick={() => answer(false)}>
                Cancel
              </button>
              <button
                type="button"
                className={`btn ${options.destructive ? 'btn--danger' : 'btn--primary'}`}
                onClick={() => answer(true)}
                autoFocus
              >
                {options.action}
              </button>
            </>
          }
        >
          <p>{options.message}</p>
        </Modal>
      )}
    </ConfirmContext.Provider>
  );
}
