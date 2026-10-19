import type { ReactNode } from 'react';
import { NavLink, Navigate, Route, Routes } from 'react-router';
import { useAdmin, useSession } from '../app/sessionContext';
import { greeting } from '../core/format';
import { HistoryIcon, PeopleIcon, PersonIcon, PhoneIcon } from '../ui/icons';
import { AccountPage } from './AccountPage';
import { AuditPage } from './AuditPage';
import { CliniciansPage } from './CliniciansPage';
import { DevicesPage } from './DevicesPage';
import { ProfileMenu } from './ProfileMenu';

const tabs: { path: string; label: string; icon: ReactNode }[] = [
  { path: '/clinicians', label: 'Clinicians', icon: <PeopleIcon /> },
  { path: '/devices', label: 'Devices', icon: <PhoneIcon /> },
  { path: '/audit', label: 'Audit log', icon: <HistoryIcon /> },
  { path: '/account', label: 'My account', icon: <PersonIcon /> },
];

/**
 * The signed-in frame, in the mobile app's style: a greeting header, and tabs with a soft-teal pill that become a
 * bottom navigation bar on a phone-sized screen.
 */
export function DashboardShell() {
  const session = useSession();
  const admin = useAdmin();

  return (
    <div className="shell">
      <header className="appbar">
        <div className="appbar__row">
          <div className="appbar__hello">
            <span className="appbar__title">
              WoundAI <span className="appbar__badge">ADMIN</span>
            </span>
            <p className="appbar__greeting">
              {greeting()},{' '}
              <span className="appbar__name" data-testid="signed-in-as">
                {admin?.username}
              </span>
            </p>
            <span className="appbar__facility">Facility {admin?.facilityId}</span>
          </div>
          <div className="appbar__user">
            <ProfileMenu />
            {/* On a phone-sized screen sign-out stays one tap away; on wider screens it is in the profile panel. */}
            <button type="button" className="btn btn--on-dark appbar__signout" onClick={() => void session.signOut()}>
              Sign out
            </button>
          </div>
        </div>
        <nav className="tabs" aria-label="Sections">
          {tabs.map((t) => (
            <NavLink key={t.path} to={t.path} className={({ isActive }) => `tab${isActive ? ' tab--active' : ''}`}>
              <span className="tab__icon">{t.icon}</span>
              <span className="tab__label">{t.label}</span>
            </NavLink>
          ))}
        </nav>
      </header>
      <main className="shell__page">
        <Routes>
          <Route path="/clinicians" element={<CliniciansPage />} />
          <Route path="/devices" element={<DevicesPage />} />
          <Route path="/audit" element={<AuditPage />} />
          <Route path="/account" element={<AccountPage />} />
          <Route path="*" element={<Navigate to="/clinicians" replace />} />
        </Routes>
      </main>
    </div>
  );
}
