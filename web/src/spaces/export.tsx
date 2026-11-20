import { lazy } from 'react';
import { Route } from 'react-router';
import { IconFileExport } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const SchedulesPage = lazy(() => import('../pages/schedules/SchedulesPage').then((m) => ({ default: m.SchedulesPage })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const PresetsPage = lazy(() => import('../pages/export/PresetsPage').then((m) => ({ default: m.PresetsPage })));

export const exportSpace: Space = {
  id: 'export',
  module: 'export',
  label: t.nav.exportSpace,
  short: t.nav.shortExport,
  icon: IconFileExport,
  pages: [
    { to: '/export/schedules', label: t.nav.schedules, section: 'work' },
    { to: '/export/history', label: t.nav.historyPage, section: 'work' },
    { to: '/export/presets', label: t.nav.presetsPage, section: 'setup' },
  ],
  routes: () => (
    <>
      <Route path="schedules/:id?" element={<RoleOnly role="Viewer"><SchedulesPage module="export" base="/export/schedules" /></RoleOnly>} />
      <Route path="history/*" element={<RoleOnly role="Viewer"><HistoryPage module="export" base="/export/history" /></RoleOnly>} />
      <Route path="presets" element={<RoleOnly role="Viewer"><Titled title={t.nav.presetsPage}><PresetsPage /></Titled></RoleOnly>} />
    </>
  ),
};
