import type { ReactNode } from 'react';
import { Navigate, useParams, useSearchParams } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { useSpaces, visiblePages } from '../spaces/useSpaces';
import { OverviewPage } from './overview/OverviewPage';

/**
 * Главный экран: не проектировщику — «Обзор»; проектировщику — первая видимая страница первого доступного пространства, а если
 * таких нет (коллизии выключены) — профиль с пояснением.
 */
export function HomeRedirect() {
  const auth = useAuth();
  const { spaces } = useSpaces();
  if (auth.isStaff) return <OverviewPage />;
  const first = spaces[0];
  return first ? <Navigate to={visiblePages(first, auth)[0].to} replace /> : <Navigate to="/profile" state={{ noSpaces: true }} replace />;
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
