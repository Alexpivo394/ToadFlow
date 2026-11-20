import { lazy } from 'react';
import { Route } from 'react-router';
import { IconServer2 } from '@tabler/icons-react';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const AgentsPage = lazy(() => import('../pages/farm/AgentsPage').then((m) => ({ default: m.AgentsPage })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const SourcesPage = lazy(() => import('../pages/farm/SourcesPage').then((m) => ({ default: m.SourcesPage })));
const NamingPage = lazy(() => import('../pages/farm/NamingPage').then((m) => ({ default: m.NamingPage })));

/** Ферма — общее для всех модулей: агенты, очередь всех заданий, источники моделей, именование моделей. */
export const farmSpace: Space = {
  id: 'farm',
  label: t.nav.farm,
  short: t.nav.shortFarm,
  icon: IconServer2,
  pages: [
    { to: '/farm/agents', label: t.nav.agents, section: 'work' },
    { to: '/farm/queue', label: t.nav.farmQueue, section: 'work' },
    { to: '/farm/sources', label: t.nav.farmSources, section: 'setup' },
    { to: '/farm/naming', label: t.nav.farmNaming, section: 'setup' },
  ],
  routes: () => (
    <>
      <Route path="agents" element={<RoleOnly role="Viewer"><Titled title={t.nav.agents}><AgentsPage /></Titled></RoleOnly>} />
      <Route path="queue/*" element={<RoleOnly role="Viewer"><HistoryPage base="/farm/queue" /></RoleOnly>} />
      <Route path="sources" element={<RoleOnly role="Viewer"><Titled title={t.nav.farmSources}><SourcesPage /></Titled></RoleOnly>} />
      <Route path="naming" element={<RoleOnly role="Viewer"><Titled title={t.nav.farmNaming}><NamingPage /></Titled></RoleOnly>} />
    </>
  ),
};
