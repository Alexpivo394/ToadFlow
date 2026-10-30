import type { ReactNode } from 'react';
import { Navigate, useParams, useSearchParams } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { DashboardPage } from './dashboard/DashboardPage';

/** Главный экран: проектировщик — в «Мои коллизии» (без разделов — в «Проверки»), остальные — «Обзор» ToadFlow. */
export function HomeRedirect() {
  const { isStaff } = useAuth();
  return isStaff ? <DashboardPage /> : <ClashRedirect />;
}

/** «Коллизии»: есть разделы — «Мои», иначе — «Проверки». */
export function ClashRedirect() {
  const { disciplines } = useAuth();
  return <Navigate to={disciplines.length > 0 ? '/clash/my' : '/clash/checks'} replace />;
}

/** Старые ссылки на часть: вкладка «Утверждённая» — страница проверки, остальное — настройка части. Коллизия переносится. */
export function OldPartRedirect() {
  const { id } = useParams();
  const [params] = useSearchParams();
  if (params.get('tab') === 'approved') {
    const collision = params.get('collision');
    return <Navigate to={`/clash/checks/${id}${collision ? `?collision=${encodeURIComponent(collision)}` : ''}`} replace />;
  }
  const query = params.toString();
  return <Navigate to={`/clash/setup/parts/${id}${query ? `?${query}` : ''}`} replace />;
}

/** Экраны ToadFlow вне коллизий: проектировщику закрыты. */
export function StaffOnly({ children }: { children: ReactNode }) {
  const { isStaff } = useAuth();
  return isStaff ? children : <Navigate to="/" replace />;
}

/** Настройка проверок — координатор и администратор. */
export function SetupOnly({ children }: { children: ReactNode }) {
  const { can } = useAuth();
  return can('Operator') ? children : <Navigate to="/clash" replace />;
}
