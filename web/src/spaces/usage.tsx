import { lazy, type ReactNode } from 'react';
import { Route } from 'react-router';
import { IconChartHistogram } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const SummaryPage = lazy(() => import('../pages/usage/SummaryPage').then((m) => ({ default: m.SummaryPage })));
const WaitsPage = lazy(() => import('../pages/usage/WaitsPage').then((m) => ({ default: m.WaitsPage })));
const ToolsPage = lazy(() => import('../pages/usage/ToolsPage').then((m) => ({ default: m.ToolsPage })));
const ToolPage = lazy(() => import('../pages/usage/ToolPage').then((m) => ({ default: m.ToolPage })));
const MachinesPage = lazy(() => import('../pages/usage/MachinesPage').then((m) => ({ default: m.MachinesPage })));
const MachinePage = lazy(() => import('../pages/usage/MachinePage').then((m) => ({ default: m.MachinePage })));
const ServicePage = lazy(() => import('../pages/usage/ServicePage').then((m) => ({ default: m.ServicePage })));
const CoveragePage = lazy(() => import('../pages/usage/CoveragePage').then((m) => ({ default: m.CoveragePage })));
const PersonPage = lazy(() => import('../pages/usage/PersonPage').then((m) => ({ default: m.PersonPage })));
const SettingsPage = lazy(() => import('../pages/usage/SettingsPage').then((m) => ({ default: m.SettingsPage })));

/** Страница «Аналитики»: от Наблюдателя, проектировщику — переход на главную. */
const viewer = (title: string, page: ReactNode) => <RoleOnly role="Viewer"><Titled title={title}>{page}</Titled></RoleOnly>;

/** Как проектировщики работают в Revit и в сервисе. Только для Наблюдателя и старше: проектировщик данные отправляет, но не видит. */
export const usageSpace: Space = {
  id: 'usage',
  module: 'usage',
  label: t.nav.usage,
  short: t.nav.shortUsage,
  icon: IconChartHistogram,
  pages: [
    { to: '/usage', label: t.usage.summary, end: true, section: 'work' },
    { to: '/usage/waits', label: t.usage.waits, section: 'work' },
    { to: '/usage/tools', label: t.usage.tools, section: 'work' },
    { to: '/usage/machines', label: t.usage.machines, section: 'work' },
    { to: '/usage/service', label: t.usage.service, section: 'work' },
    { to: '/usage/coverage', label: t.usage.coverage, section: 'work' },
    { to: '/usage/settings', label: t.usage.settings, section: 'setup' },
  ],
  routes: () => (
    <>
      <Route index element={viewer(t.nav.usage, <SummaryPage />)} />
      <Route path="waits" element={viewer(t.usage.waits, <WaitsPage />)} />
      <Route path="tools" element={viewer(t.usage.tools, <ToolsPage />)} />
      <Route path="tools/:source/:commandId" element={<RoleOnly role="Viewer"><ToolPage /></RoleOnly>} />
      <Route path="machines" element={viewer(t.usage.machines, <MachinesPage />)} />
      <Route path="machines/:key" element={<RoleOnly role="Viewer"><MachinePage /></RoleOnly>} />
      <Route path="service" element={viewer(t.usage.service, <ServicePage />)} />
      <Route path="coverage" element={viewer(t.usage.coverage, <CoveragePage />)} />
      <Route path="people/:id" element={<RoleOnly role="Viewer"><PersonPage /></RoleOnly>} />
      <Route path="settings" element={viewer(t.usage.settings, <SettingsPage />)} />
    </>
  ),
};
