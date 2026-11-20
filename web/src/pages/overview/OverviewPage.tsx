import { Paper, SimpleGrid, Stack, Title } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { OverviewView } from '../../api/types';
import { PageTitle } from '../../components/ui';
import { t } from '../../i18n/ru';
import { moduleInfo, type ModuleName } from '../../spaces/modules';
import { FarmAgents } from '../farm/AgentsPanel';
import { AttentionBlock } from './AttentionBlock';
import { ModuleTile } from './ModuleTile';
import { ServerBlock } from './ServerBlock';

/**
 * «Обзор» — состояние всего: что требует внимания (во всю ширину), плитка на включённый модуль, ферма и сервер. Обновляется
 * раз в минуту, агенты — раз в 15 с.
 */
export function OverviewPage() {
  const overview = useApi<OverviewView>(['overview'], '/api/overview', { refetchInterval: 60_000 });
  const data = overview.data;

  return (
    <>
      <PageTitle>{t.nav.overview}</PageTitle>
      <Stack gap="md">
        <AttentionBlock items={data?.attention} />
        {data && data.tiles.length > 0 && (
          <SimpleGrid cols={{ base: 1, sm: 2, md: Math.min(data.tiles.length, 4) }}>
            {data.tiles.map((tile) => (
              <ModuleTile key={tile.module} tile={tile} title={moduleInfo[tile.module as ModuleName]?.title ?? tile.module} />
            ))}
          </SimpleGrid>
        )}
        <SimpleGrid cols={{ base: 1, md: 2 }}>
          <Paper withBorder p="md" radius="md" component="section" aria-label="Ферма">
            <Title order={4} mb="xs">{t.nav.farm}</Title>
            <FarmAgents />
          </Paper>
          <ServerBlock stats={data?.server} />
        </SimpleGrid>
      </Stack>
    </>
  );
}
