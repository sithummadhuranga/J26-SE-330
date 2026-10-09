import { createContext, useContext, useSyncExternalStore } from 'react';
import type { Admin, AuthSession } from '../core/session';

export const SessionContext = createContext<AuthSession | null>(null);

export function useSession(): AuthSession {
  const session = useContext(SessionContext);
  if (!session) throw new Error('useSession outside SessionContext');
  return session;
}

/** The signed-in admin; re-renders on sign-in and sign-out. */
export function useAdmin(): Admin | null {
  const session = useSession();
  return useSyncExternalStore(session.subscribe, () => session.admin);
}
