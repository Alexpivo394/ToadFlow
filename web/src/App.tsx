import { lazy, type ReactNode } from 'react';
import { Navigate, Route, Routes, type RouteObject } from 'react-router';
import { Center, Loader } from '@mantine/core';
import { AuthProvider, useAuth } from './auth/AuthContext';
import { LoginPage } from './auth/LoginPage';
import { RegisterPage } from './auth/RegisterPage';
import { AppShell } from './layout/AppShell';
import { CheckPage } from './pages/clash/checks/CheckPage';
import { ChecksPage } from './pages/clash/checks/ChecksPage';
import { MyPage } from './pages/clash/my/MyPage';
import { ClashRedirect, HomeRedirect, OldPartRedirect, SetupOnly, StaffOnly } from './pages/HomeRedirect';
import { ProfilePage } from './pages/profile/ProfilePage';

// Страницы своими чанками: грузятся, когда их открыли. Проектировщику не нужен код настроек, расписаний и графиков,
// а вход не ждёт весь бандл. «Мои», «Проверки» и проверка — в главном чанке: с них начинают все.
const AnalyticsPage = lazy(() => import('./pages/analytics/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));
const ReportPage = lazy(() => import('./pages/clash/reports/ReportPage').then((m) => ({ default: m.ReportPage })));
const PartShell = lazy(() => import('./pages/clash/part/PartShell').then((m) => ({ default: m.PartShell })));
const OverviewPage = lazy(() => import('./pages/clash/setup/OverviewPage').then((m) => ({ default: m.OverviewPage })));
const SummaryPage = lazy(() => import('./pages/clash/summary/SummaryPage').then((m) => ({ default: m.SummaryPage })));
const ClashHome = lazy(() => import('./pages/clash/home/ClashHome').then((m) => ({ default: m.ClashHome })));
const HistoryPage = lazy(() => import('./pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const SchedulesPage = lazy(() => import('./pages/schedules/SchedulesPage').then((m) => ({ default: m.SchedulesPage })));
const SettingsPage = lazy(() => import('./pages/settings/SettingsPage').then((m) => ({ default: m.SettingsPage })));

/**
 * Маршруты data router (createBrowserRouter в приложении, createMemoryRouter в тестах): вход и страницы — внутри <c>App</c>, data
 * router нужен ради useBlocker — остановить уход со страницы с несохранённым черновиком при любом переходе, в том числе «Назад».
 * <c>before</c> — что отрисовать рядом (в тестах — щуп навигации).
 */
export function appRoutes(before?: ReactNode): RouteObject[] {
  return [
    {
      path: '*',
      element: (
        <>
          {before}
          <AuthProvider>
            <App />
          </AuthProvider>
        </>
      ),
    },
  ];
}

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
        <Route path="register" element={<RegisterPage />} />
        <Route path="*" element={<LoginPage />} />
      </Routes>
    );
  }

  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<HomeRedirect />} />
        <Route path="profile" element={<ProfilePage />} />
        <Route path="schedules/:id?" element={<StaffOnly><SchedulesPage /></StaffOnly>} />
        <Route path="history/*" element={<StaffOnly><HistoryPage /></StaffOnly>} />
        <Route path="analytics/*" element={<StaffOnly><AnalyticsPage /></StaffOnly>} />
        <Route path="clash" element={<ClashRedirect />} />
        <Route path="clash/my" element={<MyPage />} />
        <Route path="clash/checks" element={<ChecksPage />} />
        <Route path="clash/checks/:id" element={<CheckPage />} />
        <Route path="clash/reports/:runId" element={<ReportPage />} />
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
