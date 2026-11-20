import type { ReactNode } from 'react';
import { Navigate } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { PageTitle } from '../components/ui';

/** Страница с ролью не ниже указанной; остальным — переход на главную. Права те же, что у пунктов меню. */
export function RoleOnly({ role, children }: { role: 'Viewer' | 'Operator' | 'Admin'; children: ReactNode }) {
  const { can, isStaff } = useAuth();
  return isStaff && can(role) ? children : <Navigate to="/" replace />;
}

/** Страница настройки без своего заголовка — с заголовком пункта меню. */
export function Titled({ title, children }: { title: string; children: ReactNode }) {
  return (
    <>
      <PageTitle>{title}</PageTitle>
      {children}
    </>
  );
}
