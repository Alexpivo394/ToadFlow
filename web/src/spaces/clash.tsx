import { lazy } from 'react';
import { Navigate, Route } from 'react-router';
import { IconBox } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { CheckPage } from '../pages/clash/checks/CheckPage';
import { ChecksPage } from '../pages/clash/checks/ChecksPage';
import { MyPage } from '../pages/clash/my/MyPage';
import { ClashRedirect, OldPartRedirect, SetupOnly } from '../pages/HomeRedirect';
import { Titled } from './guards';
import type { Space } from './types';

// «Мои», «Проверки» и проверка — в главном чанке: с них начинают проектировщики. Остальное грузится, когда открыли.
const ReportPage = lazy(() => import('../pages/clash/reports/ReportPage').then((m) => ({ default: m.ReportPage })));
const PartShell = lazy(() => import('../pages/clash/part/PartShell').then((m) => ({ default: m.PartShell })));
const OverviewPage = lazy(() => import('../pages/clash/setup/OverviewPage').then((m) => ({ default: m.OverviewPage })));
const SummaryPage = lazy(() => import('../pages/clash/summary/SummaryPage').then((m) => ({ default: m.SummaryPage })));
const ClashHome = lazy(() => import('../pages/clash/home/ClashHome').then((m) => ({ default: m.ClashHome })));
const SchedulesPage = lazy(() => import('../pages/schedules/SchedulesPage').then((m) => ({ default: m.SchedulesPage })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const ClashSettingsPage = lazy(() => import('../pages/clash/setup/ClashSettingsPage').then((m) => ({ default: m.ClashSettingsPage })));

/** Адреса /clash/... стоят в разосланных отчётах HTML и в письмах — они не меняются. */
export const clashSpace: Space = {
  id: 'clash',
  module: 'clash',
  label: t.nav.clash,
  short: t.nav.shortClash,
  icon: IconBox,
  pages: [
    { to: '/clash/my', label: t.nav.clashMy, designer: true, hidden: (auth) => auth.disciplines.length === 0, section: 'work' },
    { to: '/clash/checks', label: t.nav.clashChecks, designer: true, section: 'work' },
    { to: '/clash/summary', label: t.nav.clashSummary, designer: true, section: 'work' },
    { to: '/clash/setup', label: t.nav.setupOverview, role: 'Operator', end: true, section: 'setup' },
    { to: '/clash/setup/parts', label: t.nav.setupParts, role: 'Operator', section: 'setup' },
    { to: '/clash/setup/templates', label: t.nav.setupTemplates, role: 'Operator', section: 'setup' },
    { to: '/clash/setup/snapshots', label: t.nav.setupSnapshots, role: 'Operator', section: 'setup' },
    { to: '/clash/setup/schedules', label: t.nav.setupSchedules, role: 'Operator', section: 'setup' },
    { to: '/clash/setup/history', label: t.nav.setupHistory, role: 'Operator', section: 'setup' },
    { to: '/clash/setup/settings', label: t.nav.setupSettings, role: 'Operator', section: 'setup' },
  ],
  routes: () => (
    <>
      <Route index element={<ClashRedirect />} />
      <Route path="my" element={<MyPage />} />
      <Route path="checks" element={<ChecksPage />} />
      <Route path="checks/:id" element={<CheckPage />} />
      <Route path="reports/:runId" element={<ReportPage />} />
      <Route path="summary" element={<SummaryPage />} />
      <Route path="setup" element={<SetupOnly><OverviewPage /></SetupOnly>} />
      <Route path="setup/parts" element={<SetupOnly><ClashHome tab="checks" /></SetupOnly>} />
      <Route path="setup/snapshots" element={<SetupOnly><ClashHome tab="uploads" /></SetupOnly>} />
      <Route path="setup/templates" element={<SetupOnly><ClashHome tab="templates" /></SetupOnly>} />
      <Route path="setup/parts/:id" element={<SetupOnly><PartShell /></SetupOnly>} />
      <Route path="setup/schedules/:id?" element={<SetupOnly><SchedulesPage module="clash" base="/clash/setup/schedules" /></SetupOnly>} />
      <Route path="setup/history/*" element={<SetupOnly><HistoryPage module="clash" base="/clash/setup/history" /></SetupOnly>} />
      <Route path="setup/settings" element={<SetupOnly><Titled title={t.nav.setupSettings}><ClashSettingsPage /></Titled></SetupOnly>} />
      {/* Старые адреса из чата и закладок. */}
      <Route path="parts/:id" element={<OldPartRedirect />} />
      <Route path="snapshots" element={<Navigate to="/clash/setup/snapshots" replace />} />
      <Route path="templates" element={<Navigate to="/clash/setup/templates" replace />} />
    </>
  ),
};
