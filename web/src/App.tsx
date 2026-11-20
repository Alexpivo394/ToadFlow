import { lazy, type ReactNode } from 'react';
import { Navigate, Route, Routes, type RouteObject } from 'react-router';
import { Center, Loader } from '@mantine/core';
import { AuthProvider, useAuth } from './auth/AuthContext';
import { LoginPage } from './auth/LoginPage';
import { RegisterPage } from './auth/RegisterPage';
import { AppShell } from './layout/AppShell';
import { HomeRedirect, StaffOnly } from './pages/HomeRedirect';
import { ProfilePage } from './pages/profile/ProfilePage';
import { useSpaces } from './spaces/useSpaces';

// Страницы своими чанками: грузятся, когда их открыли (страницы пространств — в манифестах web/src/spaces).
const AnalyticsPage = lazy(() => import('./pages/analytics/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));
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
  const { spaces, loading: spacesLoading } = useSpaces();

  if (loading || (user && spacesLoading)) {
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

  // Маршруты — из манифестов пространств, которые видит пользователь: у выключенного модуля их нет, его адреса ведут на главную.
  return (
    <Routes>
      <Route element={<AppShell spaces={spaces} />}>
        <Route index element={<HomeRedirect />} />
        <Route path="profile" element={<ProfilePage />} />
        {spaces.map((space) => (
          <Route key={space.id} path={space.id}>
            {space.routes()}
          </Route>
        ))}
        {/* Прежние адреса — до перенаправлений на новые места (задача 15). */}
        <Route path="schedules/:id?" element={<StaffOnly><SchedulesPage /></StaffOnly>} />
        <Route path="history/*" element={<StaffOnly><HistoryPage /></StaffOnly>} />
        <Route path="analytics/*" element={<StaffOnly><AnalyticsPage /></StaffOnly>} />
        <Route path="settings" element={<StaffOnly><Navigate to="agents" replace /></StaffOnly>} />
        <Route path="settings/:section" element={<StaffOnly><SettingsPage /></StaffOnly>} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
