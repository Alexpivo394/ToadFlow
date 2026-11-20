import { Link } from 'react-router';
import { Anchor, SimpleGrid, Stack, Table, Text, Tooltip } from '@mantine/core';
import { BarChart } from '@mantine/charts';
import { useApi } from '../../api/queries';
import type { FarmSummary } from '../../api/types';
import { Empty, Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { moduleInfo, type ModuleName } from '../../spaces/modules';

/**
 * Сводка «Фермы → Очередь и журнал» по всем включённым модулям: очередь, модели по дням, ближайшие запуски, последние ошибки
 * (раньше — на главной). Обновляется раз в минуту.
 */
export function QueueSummary() {
  const summary = useApi<FarmSummary>(['farm-summary'], '/api/farm/summary', { refetchInterval: 60_000 });
  const data = summary.data;

  return (
    <Stack gap="md" mb="md">
      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <Panel title="Модели по дням (14 дней)">
          {data && <Text size="sm" c="dimmed" mb="xs">{data.kpi.queued} в очереди · {data.kpi.running} в работе</Text>}
          <BarChart
            h={160}
            data={(data?.daily ?? []).map((d) => ({ date: d.date.slice(8, 10) + '.' + d.date.slice(5, 7), Успешно: d.succeeded, 'С ошибкой': d.failed }))}
            dataKey="date"
            type="stacked"
            series={[
              { name: 'Успешно', color: 'green.6' },
              { name: 'С ошибкой', color: 'red.6' },
            ]}
            withLegend
            tickLine="none"
            gridAxis="none"
          />
        </Panel>
        <Panel title="Ближайшие запуски">
          {data && data.upcoming.length === 0 ? (
            <Empty>Включённых расписаний нет</Empty>
          ) : (
            <Table>
              <Table.Tbody>
                {data?.upcoming.map((u) => (
                  <Table.Tr key={u.scheduleId}>
                    <Table.Td>{formatDateTime(u.at)}</Table.Td>
                    <Table.Td>
                      <Anchor component={Link} to={`${(moduleInfo[u.module as ModuleName] ?? moduleInfo.export).schedules}/${u.scheduleId}`} size="sm">
                        {u.name}
                      </Anchor>
                    </Table.Td>
                    <Table.Td c="dimmed">{u.modelCount} шт.</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </Panel>
      </SimpleGrid>
      <Panel title="Последние ошибки">
        {data && data.recentErrors.length === 0 ? (
          <Empty>Ошибок нет</Empty>
        ) : (
          <Table.ScrollContainer minWidth={600}>
            <Table>
              <Table.Tbody>
                {data?.recentErrors.map((e) => (
                  <Table.Tr key={e.jobId}>
                    <Table.Td c="dimmed">{formatDateTime(e.at)}</Table.Td>
                    <Table.Td>
                      <Anchor component={Link} to={`/farm/queue/runs/${e.runId}?job=${e.jobId}`} size="sm">{e.modelName}</Anchor>
                    </Table.Td>
                    <Table.Td c="red">
                      <Tooltip label={e.note} disabled={!e.note} multiline maw={400}>
                        <Text size="sm" lineClamp={1}>{e.note}</Text>
                      </Tooltip>
                    </Table.Td>
                    <Table.Td c="dimmed">{[e.agentName, e.revitYear ? `R${e.revitYear}` : null].filter(Boolean).join(' · ')}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Panel>
    </Stack>
  );
}
