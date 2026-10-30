import { Navigate, Route, Routes } from 'react-router';
import { Center, Loader } from '@mantine/core';
import { useAuth } from './auth/AuthContext';
import { LoginPage } from './auth/LoginPage';
import { AppShell } from './layout/AppShell';
import { AnalyticsPage } from './pages/analytics/AnalyticsPage';
import { CheckPage } from './pages/clash/checks/CheckPage';
import { ChecksPage } from './pages/clash/checks/ChecksPage';
import { ClashHome } from './pages/clash/home/ClashHome';
import { MyPage } from './pages/clash/my/MyPage';
import { PartShell } from './pages/clash/part/PartShell';
import { OverviewPage } from './pages/clash/setup/OverviewPage';
import { SummaryPage } from './pages/clash/summary/SummaryPage';
import { ClashRedirect, HomeRedirect, OldPartRedirect, SetupOnly, StaffOnly } from './pages/HomeRedirect';
import { HistoryPage } from './pages/history/HistoryPage';
import { SchedulesPage } from './pages/schedules/SchedulesPage';
import { SettingsPage } from './pages/settings/SettingsPage';

export function App() {
  const { user, loading } = useAuth();

  if (loading) {
    return (
      <Center mih="100vh">
        <Loader />
      </Center>
    );
  }

  if (!user) {
    return (
      <Routes>
        <Route path="*" element={<LoginPage />} />
      </Routes>
    );
  }

  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<HomeRedirect />} />
        <Route path="schedules/:id?" element={<StaffOnly><SchedulesPage /></StaffOnly>} />
        <Route path="history/*" element={<StaffOnly><HistoryPage /></StaffOnly>} />
        <Route path="analytics/*" element={<StaffOnly><AnalyticsPage /></StaffOnly>} />
        <Route path="clash" element={<ClashRedirect />} />
        <Route path="clash/my" element={<MyPage />} />
        <Route path="clash/checks" element={<ChecksPage />} />
        <Route path="clash/checks/:id" element={<CheckPage />} />
        <Route path="clash/summary" element={<SummaryPage />} />
        <Route path="clash/setup" element={<SetupOnly><OverviewPage /></SetupOnly>} />
        <Route path="clash/setup/parts" element={<SetupOnly><ClashHome tab="checks" /></SetupOnly>} />
        <Route path="clash/setup/snapshots" element={<SetupOnly><ClashHome tab="uploads" /></SetupOnly>} />
        <Route path="clash/setup/templates" element={<SetupOnly><ClashHome tab="templates" /></SetupOnly>} />
        <Route path="clash/setup/parts/:id" element={<SetupOnly><PartShell /></SetupOnly>} />
        {/* Старые адреса из чата и закладок. */}
        <Route path="clash/parts/:id" element={<OldPartRedirect />} />
        <Route path="clash/snapshots" element={<Navigate to="/clash/setup/snapshots" replace />} />
        <Route path="clash/templates" element={<Navigate to="/clash/setup/templates" replace />} />
        <Route path="settings" element={<StaffOnly><Navigate to="agents" replace /></StaffOnly>} />
        <Route path="settings/:section" element={<StaffOnly><SettingsPage /></StaffOnly>} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
