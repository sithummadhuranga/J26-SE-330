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
// A reload keeps the tab's refresh token; resume that session instead of asking for a password again.
const restoring = session.restore();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App session={session} restoring={restoring} />
    </BrowserRouter>
  </StrictMode>,
);
