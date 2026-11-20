import { lazy } from 'react';
import { Route } from 'react-router';
import { IconFileExport } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const SchedulesPage = lazy(() => import('../pages/schedules/SchedulesPage').then((m) => ({ default: m.SchedulesPage })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const PresetsSettings = lazy(() => import('../pages/settings/PresetsSettings').then((m) => ({ default: m.PresetsSettings })));

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
      <Route path="schedules/:id?" element={<RoleOnly role="Viewer"><SchedulesPage /></RoleOnly>} />
      <Route path="history/*" element={<RoleOnly role="Viewer"><HistoryPage /></RoleOnly>} />
      <Route path="presets" element={<RoleOnly role="Viewer"><Titled title={t.nav.presetsPage}><PresetsSettings /></Titled></RoleOnly>} />
    </>
  ),
};
