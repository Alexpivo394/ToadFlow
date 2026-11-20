import type { ReactNode } from 'react';
import { Navigate, Route, Routes, type RouteObject } from 'react-router';
import { Center, Loader } from '@mantine/core';
import { AuthProvider, useAuth } from './auth/AuthContext';
import { LoginPage } from './auth/LoginPage';
import { RegisterPage } from './auth/RegisterPage';
import { AppShell } from './layout/AppShell';
import { HomeRedirect } from './pages/HomeRedirect';
import { ProfilePage } from './pages/profile/ProfilePage';
import { oldRoutes } from './pages/redirects';
import { useSpaces } from './spaces/useSpaces';


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
        {/* Прежние адреса из закладок и ссылок — на новые места. */}
        {oldRoutes()}
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
