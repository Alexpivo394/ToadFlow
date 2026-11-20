import { lazy } from 'react';
import { Route } from 'react-router';
import { IconSettings } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const UsersPage = lazy(() => import('../pages/admin/UsersPage').then((m) => ({ default: m.UsersPage })));
const ModulesPage = lazy(() => import('../pages/admin/ModulesPage').then((m) => ({ default: m.ModulesPage })));
const ServerPage = lazy(() => import('../pages/admin/ServerPage').then((m) => ({ default: m.ServerPage })));
const EventsPage = lazy(() => import('../pages/admin/EventsPage').then((m) => ({ default: m.EventsPage })));

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
      <Route path="modules" element={<RoleOnly role="Viewer"><Titled title={t.nav.modules}><ModulesPage /></Titled></RoleOnly>} />
      <Route path="server" element={<RoleOnly role="Viewer"><Titled title={t.nav.server}><ServerPage /></Titled></RoleOnly>} />
      <Route path="events" element={<RoleOnly role="Viewer"><Titled title={t.nav.events}><EventsPage /></Titled></RoleOnly>} />
    </>
  ),
};
