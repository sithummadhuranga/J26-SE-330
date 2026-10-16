import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { App } from './app/App';
import { AdminApi } from './core/api';
import { apiBaseUrl } from './core/config';
import { AuthSession } from './core/session';
import { sessionStorageStore } from './core/tokenStore';
import './styles.css';

const session = new AuthSession(new AdminApi(apiBaseUrl), sessionStorageStore);
// Back from the identity service's sign-in page: finish it and drop the code from the address bar. Otherwise a
// reload keeps the tab's refresh token, so resume that session instead of asking to sign in again.
const restoring =
  window.location.pathname === '/callback'
    ? session.completeSignIn(window.location.href).finally(() => window.history.replaceState(null, '', '/'))
    : session.restore();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App session={session} restoring={restoring} />
    </BrowserRouter>
  </StrictMode>,
);
