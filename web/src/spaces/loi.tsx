import { lazy } from 'react';
import { Route } from 'react-router';
import { IconChecklist } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const AnalyticsPage = lazy(() => import('../pages/analytics/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));
const SchedulesPage = lazy(() => import('../pages/schedules/SchedulesPage').then((m) => ({ default: m.SchedulesPage })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const LoiSettings = lazy(() => import('../pages/loi/settings/LoiSettings').then((m) => ({ default: m.LoiSettings })));
const AnalyticsSettings = lazy(() => import('../pages/loi/settings/AnalyticsSettings').then((m) => ({ default: m.AnalyticsSettings })));

export const loiSpace: Space = {
  id: 'loi',
  module: 'loi',
  label: t.nav.loiSpace,
  short: t.nav.shortLoi,
  icon: IconChecklist,
  pages: [
    { to: '/loi/analytics', label: t.nav.analyticsPage, designer: true, section: 'work' },
    { to: '/loi/schedules', label: t.nav.schedules, section: 'work' },
    { to: '/loi/history', label: t.nav.historyPage, section: 'work' },
    { to: '/loi/profiles', label: t.nav.profilesPage, section: 'setup' },
    { to: '/loi/analytics-settings', label: t.nav.analyticsSettings, section: 'setup' },
  ],
  routes: () => (
    <>
      {/* Аналитику смотрят и проектировщики: на странице нет ничего, что меняет настройки. */}
      <Route path="analytics/*" element={<AnalyticsPage />} />
      <Route path="schedules/:id?" element={<RoleOnly role="Viewer"><SchedulesPage module="loi" base="/loi/schedules" /></RoleOnly>} />
      <Route path="history/*" element={<RoleOnly role="Viewer"><HistoryPage module="loi" base="/loi/history" /></RoleOnly>} />
      <Route path="profiles" element={<RoleOnly role="Viewer"><Titled title={t.nav.profilesPage}><LoiSettings /></Titled></RoleOnly>} />
      <Route path="analytics-settings" element={<RoleOnly role="Viewer"><Titled title={t.nav.analyticsSettings}><AnalyticsSettings /></Titled></RoleOnly>} />
    </>
  ),
};
