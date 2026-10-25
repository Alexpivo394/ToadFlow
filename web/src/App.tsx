import { Navigate, Route, Routes } from 'react-router';
import { Center, Loader } from '@mantine/core';
import { useAuth } from './auth/AuthContext';
import { LoginPage } from './auth/LoginPage';
import { AppShell } from './layout/AppShell';
import { DashboardPage } from './pages/dashboard/DashboardPage';
import { AnalyticsPage } from './pages/analytics/AnalyticsPage';
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
        <Route index element={<DashboardPage />} />
        <Route path="schedules/:id?" element={<SchedulesPage />} />
        <Route path="history/*" element={<HistoryPage />} />
        <Route path="analytics/*" element={<AnalyticsPage />} />
        <Route path="settings">
          <Route index element={<Navigate to="agents" replace />} />
          <Route path=":section" element={<SettingsPage />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
