import { lazy } from 'react';
import { Route } from 'react-router';
import { IconServer2 } from '@tabler/icons-react';
import { useAuth } from '../auth/AuthContext';
import { t } from '../i18n/ru';
import { RoleOnly, Titled } from './guards';
import type { Space } from './types';

const AgentsSettings = lazy(() => import('../pages/settings/AgentsSettings').then((m) => ({ default: m.AgentsSettings })));
const HistoryPage = lazy(() => import('../pages/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const SourcesSettings = lazy(() => import('../pages/settings/SourcesSettings').then((m) => ({ default: m.SourcesSettings })));
const NamingEditor = lazy(() => import('../pages/settings/loi/NamingEditor').then((m) => ({ default: m.NamingEditor })));

function Naming() {
  const { can } = useAuth();
  return <NamingEditor readOnly={!can('Admin')} />;
}

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
      <Route path="agents" element={<RoleOnly role="Viewer"><Titled title={t.nav.agents}><AgentsSettings /></Titled></RoleOnly>} />
      <Route path="queue/*" element={<RoleOnly role="Viewer"><HistoryPage /></RoleOnly>} />
      <Route path="sources" element={<RoleOnly role="Viewer"><Titled title={t.nav.farmSources}><SourcesSettings /></Titled></RoleOnly>} />
      <Route path="naming" element={<RoleOnly role="Viewer"><Titled title={t.nav.farmNaming}><Naming /></Titled></RoleOnly>} />
    </>
  ),
};
