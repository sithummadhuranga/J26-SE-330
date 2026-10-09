import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import type { AuthSession } from '../core/session';
import { DashboardShell } from '../features/DashboardShell';
import { LoginScreen } from '../features/LoginScreen';
import { Centered, Spinner } from '../ui/components';
import { ConfirmProvider } from '../ui/ConfirmProvider';
import { ToastProvider } from '../ui/ToastProvider';
import { SessionContext, useAdmin } from './sessionContext';

/** The session handles 401s itself, so queries never retry on their own. */
function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false, staleTime: 0 } },
  });
}

/** After a reload, waits for the saved session to come back so the login screen doesn't flash first. */
export function App({ session, restoring }: { session: AuthSession; restoring?: Promise<unknown> }) {
  const [queryClient] = useState(createQueryClient);
  const [ready, setReady] = useState(!restoring);

  useEffect(() => {
    if (!restoring) return;
    let live = true;
    void restoring.catch(() => undefined).finally(() => live && setReady(true));
    return () => {
      live = false;
    };
  }, [restoring]);

  // A different admin may sign in next in this tab: never show them the previous one's lists.
  useEffect(
    () =>
      session.subscribe(() => {
        if (!session.signedIn) queryClient.clear();
      }),
    [session, queryClient],
  );

  return (
    <SessionContext.Provider value={session}>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <ConfirmProvider>{ready ? <Screens /> : <Loading />}</ConfirmProvider>
        </ToastProvider>
      </QueryClientProvider>
    </SessionContext.Provider>
  );
}

function Screens() {
  return useAdmin() ? <DashboardShell /> : <LoginScreen />;
}

function Loading() {
  return (
    <Centered>
      <Spinner />
    </Centered>
  );
}
