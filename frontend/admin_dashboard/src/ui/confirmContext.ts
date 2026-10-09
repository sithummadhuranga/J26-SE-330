import { createContext, useContext } from 'react';

export interface ConfirmOptions {
  title: string;
  message: string;
  action: string;
  destructive?: boolean;
}

/** Asks before an action that changes someone's access; resolves true only on an explicit yes. */
export type Confirm = (options: ConfirmOptions) => Promise<boolean>;

export const ConfirmContext = createContext<Confirm | null>(null);

export function useConfirm(): Confirm {
  const confirm = useContext(ConfirmContext);
  if (!confirm) throw new Error('useConfirm outside ConfirmProvider');
  return confirm;
}
