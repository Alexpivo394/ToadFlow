import { lazy } from 'react';
import { Navigate, Route } from 'react-router';
import { IconSettings } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const UsersPage = lazy(() => import('../pages/admin/UsersPage').then((m) => ({ default: m.UsersPage })));
const GeneralSettingsForm = lazy(() => import('../pages/admin/GeneralSettingsForm').then((m) => ({ default: m.GeneralSettingsForm })));

/** Администрирование: пользователи, модули, сервер, журнал действий. */
export const adminSpace: Space = {
  id: 'admin',
  label: t.nav.admin,
  short: t.nav.shortAdmin,
  icon: IconSettings,
  pages: [
    { to: '/admin/users', label: t.nav.users, role: 'Operator', section: 'work' },
    { to: '/admin/modules', label: t.nav.modules, section: 'work' },
    { to: '/admin/server', label: t.nav.server, section: 'work' },
    { to: '/admin/events', label: t.nav.events, section: 'work' },
  ],
  routes: () => (
    <>
      <Route path="users" element={<RoleOnly role="Operator"><Titled title={t.nav.users}><UsersPage /></Titled></RoleOnly>} />
      {/* Страницы «Модули» и «Журнал действий» — задача 19; до неё ведут на «Сервер» и прежнюю вкладку событий. */}
      <Route path="modules" element={<Navigate to="/admin/server" replace />} />
      <Route path="server" element={<RoleOnly role="Viewer"><Titled title={t.nav.server}><GeneralSettingsForm /></Titled></RoleOnly>} />
      <Route path="events" element={<Navigate to="/farm/queue/events" replace />} />
    </>
  ),
};
