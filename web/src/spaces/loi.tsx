import { lazy } from 'react';
import { Route } from 'react-router';
import { IconChecklist } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const AnalyticsPage = lazy(() => import('../pages/analytics/AnalyticsPage').then((m) => ({ default: m.AnalyticsPage })));
const SchedulesPage = lazy(() => import('../pages/schedules/SchedulesPage').then((m) => ({ default: m.SchedulesPage })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const LoiSettings = lazy(() => import('../pages/settings/loi/LoiSettings').then((m) => ({ default: m.LoiSettings })));
const AnalyticsSettings = lazy(() => import('../pages/settings/loi/AnalyticsSettings').then((m) => ({ default: m.AnalyticsSettings })));

export const loiSpace: Space = {
  id: 'loi',
  module: 'loi',
  label: t.nav.loiSpace,
  short: t.nav.shortLoi,
  icon: IconChecklist,
  pages: [
    { to: '/loi/analytics', label: t.nav.analyticsPage, section: 'work' },
    { to: '/loi/schedules', label: t.nav.schedules, section: 'work' },
    { to: '/loi/history', label: t.nav.historyPage, section: 'work' },
    { to: '/loi/profiles', label: t.nav.profilesPage, section: 'setup' },
    { to: '/loi/analytics-settings', label: t.nav.analyticsSettings, section: 'setup' },
  ],
  routes: () => (
    <>
      <Route path="analytics/*" element={<RoleOnly role="Viewer"><AnalyticsPage /></RoleOnly>} />
      <Route path="schedules/:id?" element={<RoleOnly role="Viewer"><SchedulesPage /></RoleOnly>} />
      <Route path="history/*" element={<RoleOnly role="Viewer"><HistoryPage /></RoleOnly>} />
      <Route path="profiles" element={<RoleOnly role="Viewer"><Titled title={t.nav.profilesPage}><LoiSettings /></Titled></RoleOnly>} />
      <Route path="analytics-settings" element={<RoleOnly role="Viewer"><Titled title={t.nav.analyticsSettings}><AnalyticsSettings /></Titled></RoleOnly>} />
    </>
  ),
};
