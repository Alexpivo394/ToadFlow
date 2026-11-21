import { Paper, SimpleGrid, Stack } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { OverviewView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { PageTitle } from '../../components/ui';
import { t } from '../../i18n/ru';
import { moduleInfo, type ModuleName } from '../../spaces/modules';
import { useModules } from '../../spaces/useSpaces';
import { AgentsNow } from './AgentsNow';
import { AttentionBlock } from './AttentionBlock';
import { HeroBanner } from './HeroBanner';
import { HourlyChart } from './HourlyChart';
import { LiveFeed } from './LiveFeed';
import { ModuleTile } from './ModuleTile';
import { ServerBlock } from './ServerBlock';
import './overview.css';

/**
 * «Обзор» — пульт системы: шапка с тем, как дела, и числами за сутки; что требует внимания; плитка на включённый модуль с
 * графиком за две недели; что делают агенты, задания по часам, сервер и живая лента. Обновляется по живым событиям и раз в минуту.
 */
export function OverviewPage() {
  const overview = useApi<OverviewView>(['overview'], '/api/overview', { refetchInterval: 60_000 });
  const data = overview.data;
  const { user } = useAuth();
  const modules = useModules();
  const titleOf = (module: string) => moduleInfo[module as ModuleName]?.title ?? modules.find((m) => m.name === module)?.title ?? module;

  return (
    <>
      <PageTitle>{t.nav.overview}</PageTitle>
      <Stack gap="md">
        {data?.pulse && <HeroBanner pulse={data.pulse} attention={data.attention} tiles={data.tiles} userName={user?.displayName || user?.login || ''} />}
        <AttentionBlock items={data?.attention} />
        {data && data.tiles.length > 0 && (
          <SimpleGrid cols={{ base: 1, sm: 2, lg: Math.min(data.tiles.length, 4) }}>
            {data.tiles.map((tile) => (
              <ModuleTile key={tile.module} tile={tile} title={titleOf(tile.module)} />
            ))}
          </SimpleGrid>
        )}
        <div className="ov-columns">
          <Paper withBorder p="md" radius="lg">
            <Stack gap="lg">
              <AgentsNow />
              {data?.pulse && <HourlyChart hourly={data.pulse.hourly} />}
            </Stack>
          </Paper>
          <Stack gap="md">
            <ServerBlock stats={data?.server} />
            <LiveFeed />
          </Stack>
        </div>
      </Stack>
    </>
  );
}
