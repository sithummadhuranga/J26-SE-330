import { NavLink, Navigate, Route, Routes } from 'react-router';
import { useAdmin, useSession } from '../app/sessionContext';
import { AuditPage } from './AuditPage';
import { CliniciansPage } from './CliniciansPage';
import { DevicesPage } from './DevicesPage';

const tabs = [
  { path: '/clinicians', label: 'Clinicians' },
  { path: '/devices', label: 'Devices' },
  { path: '/audit', label: 'Audit log' },
];

/** The signed-in frame: navigation, current user and facility, and sign-out. */
export function DashboardShell() {
  const session = useSession();
  const admin = useAdmin();

  return (
    <div className="shell">
      <header className="appbar">
        <div className="appbar__row">
          <span className="appbar__title">
            Wound CDSS <span className="appbar__badge">ADMIN</span>
          </span>
          <div className="appbar__user">
            <span className="appbar__name" data-testid="signed-in-as">
              {admin?.username}
            </span>
            <span className="appbar__facility">Facility {admin?.facilityId}</span>
          </div>
          <button type="button" className="btn btn--on-dark" onClick={() => void session.signOut()}>
            Sign out
          </button>
        </div>
        <nav className="tabs" aria-label="Sections">
          {tabs.map((t) => (
            <NavLink key={t.path} to={t.path} className={({ isActive }) => `tab${isActive ? ' tab--active' : ''}`}>
              {t.label}
            </NavLink>
          ))}
        </nav>
      </header>
      <main className="shell__page">
        <Routes>
          <Route path="/clinicians" element={<CliniciansPage />} />
          <Route path="/devices" element={<DevicesPage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="*" element={<Navigate to="/clinicians" replace />} />
        </Routes>
      </main>
    </div>
  );
}
