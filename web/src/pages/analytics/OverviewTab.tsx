import { Link } from 'react-router';
import { Alert, Anchor, Loader, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { BarChart, LineChart } from '@mantine/charts';
import { useApi } from '../../api/queries';
import type { AnalyticsOverview, GroupStats, MatrixCell } from '../../api/types';
import { Empty, Panel } from '../../components/ui';
import { formatShare } from '../../lib/loi';
import { shareColor, toApiQuery, toPercent } from './filters';
import { Delta, orNone, Share, Stat, useAnalyticsFilters } from './parts';

const percent = (value: number) => `${value.toString().replace('.', ',')} %`;

function GroupChart({ groups, title }: { groups: GroupStats[]; title: string }) {
  return (
    <Panel title={title}>
      {groups.length === 0 ? <Empty>Нет данных</Empty> : (
        <BarChart
          h={Math.max(140, groups.length * 34)}
          data={groups.map((g) => ({ name: orNone(g.key), Заполненность: toPercent(g.fillRate) ?? 0, Полнота: toPercent(g.completeness) ?? 0 }))}
          dataKey="name"
          orientation="vertical"
          yAxisProps={{ width: 130 }}
          xAxisProps={{ domain: [0, 100] }}
          valueFormatter={percent}
          series={[{ name: 'Заполненность', color: 'blue.6' }, { name: 'Полнота', color: 'teal.5' }]}
          withLegend
          gridAxis="x"
        />
      )}
    </Panel>
  );
}

/** Объекты по строкам, разделы по столбцам, в ячейке — заполненность: сразу видно, где провал. */
function Matrix({ cells }: { cells: MatrixCell[] }) {
  const objects = [...new Set(cells.map((c) => orNone(c.object)))].sort((a, b) => a.localeCompare(b, 'ru'));
  const disciplines = [...new Set(cells.map((c) => orNone(c.discipline)))].sort((a, b) => a.localeCompare(b, 'ru'));
  const cell = (o: string, d: string) => cells.find((c) => orNone(c.object) === o && orNone(c.discipline) === d);

  return (
    <Panel title="Объекты × разделы">
      {cells.length === 0 ? <Empty>Нет данных</Empty> : (
        <Table.ScrollContainer minWidth={400}>
          <Table withColumnBorders fz="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th />
                {disciplines.map((d) => <Table.Th key={d} ta="center">{d}</Table.Th>)}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {objects.map((o) => (
                <Table.Tr key={o}>
                  <Table.Td fw={600}>{o}</Table.Td>
                  {disciplines.map((d) => {
                    const value = cell(o, d);
                    return (
                      <Table.Td key={d} ta="center" title={value ? `моделей: ${value.models}` : undefined}
                        style={value?.fillRate != null ? { background: `var(--mantine-color-${shareColor(value.fillRate)}-light)` } : undefined}>
                        {value ? formatShare(value.fillRate) : ''}
                      </Table.Td>
                    );
                  })}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Panel>
  );
}

function Trend({ data }: { data: AnalyticsOverview }) {
  const label = (bucket: string) => `${bucket.slice(8, 10)}.${bucket.slice(5, 7)}`;
  const series = [...new Set(data.trendByObject.map((p) => p.series ?? ''))];
  const buckets = [...new Set(data.trendByObject.map((p) => p.bucket))].sort();
  const byObject = buckets.map((bucket) => ({
    bucket: label(bucket),
    ...Object.fromEntries(series.map((s) => [s, toPercent(data.trendByObject.find((p) => p.bucket === bucket && (p.series ?? '') === s)?.fillRate)])),
  }));
  const colors = ['blue.6', 'grape.6', 'orange.6', 'teal.6', 'red.6', 'lime.7'];

  return (
    <SimpleGrid cols={{ base: 1, md: 2 }}>
      <Panel title={`Заполненность по ${data.granularity === 'week' ? 'неделям' : 'дням'}`}>
        {data.trend.length === 0 ? <Empty>Нет проверок</Empty> : (
          <LineChart h={220}
            data={data.trend.map((p) => ({ bucket: label(p.bucket), Заполненность: toPercent(p.fillRate), Полнота: toPercent(p.completeness), Проверок: p.checks }))}
            dataKey="bucket" yAxisProps={{ domain: [0, 100] }} valueFormatter={percent} connectNulls withLegend
            series={[{ name: 'Заполненность', color: 'blue.6' }, { name: 'Полнота', color: 'teal.5' }]} />
        )}
      </Panel>
      <Panel title="Заполненность по объектам">
        {byObject.length === 0 ? <Empty>Нет проверок</Empty> : (
          <LineChart h={220} data={byObject} dataKey="bucket" yAxisProps={{ domain: [0, 100] }} valueFormatter={percent} connectNulls withLegend
            series={series.map((s, i) => ({ name: s, color: colors[i % colors.length] }))} />
        )}
      </Panel>
    </SimpleGrid>
  );
}

export function OverviewTab() {
  const [state] = useAnalyticsFilters();
  const query = toApiQuery(state);
  const overview = useApi<AnalyticsOverview>(['loi-analytics', 'overview', query], `/api/loi/analytics/overview?${query}`);
  const data = overview.data;

  if (overview.error) return <Alert color="red">{overview.error.message}</Alert>;
  if (!data) return <Loader />;
  if (data.models === 0) return <Empty>За выбранный период проверок нет</Empty>;

  return (
    <Stack>
      <SimpleGrid cols={{ base: 2, md: 4 }}>
        <Stat title="Заполненность" value={<Share value={data.fillRate} size="xl" />} hint="доля заполненных значений" />
        <Stat title="Полнота" value={<Share value={data.completeness} size="xl" />} hint="доля элементов, где заполнено всё" />
        <Stat title="Моделей" value={data.models} hint="по последней проверке в периоде" />
        <Stat title="Проверок" value={data.checks} hint={data.manualChecks ? `из них вручную — ${data.manualChecks}` : 'все по расписанию'} />
      </SimpleGrid>
      <Trend data={data} />
      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <GroupChart groups={data.byObject} title="По объектам" />
        <GroupChart groups={data.byDiscipline} title="По разделам" />
      </SimpleGrid>
      <Matrix cells={data.matrix} />
      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <Panel title="Отстающие модели">
          <Table fz="sm">
            <Table.Tbody>
              {data.worstModels.map((m) => (
                <Table.Tr key={m.modelKey}>
                  <Table.Td>
                    <Anchor component={Link} to={`/analytics/model?key=${encodeURIComponent(m.modelKey)}`}>{m.modelName}</Anchor>
                    <Text size="xs" c="dimmed">{[m.object, m.discipline, m.part].filter(Boolean).join(' · ')}</Text>
                  </Table.Td>
                  <Table.Td ta="right"><Share value={m.fillRate} /></Table.Td>
                  <Table.Td ta="right"><Delta now={m.fillRate} before={m.previousFillRate} profileChanged={m.profileChanged} /></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
        <Panel title="Хуже всего заполненные параметры">
          <Table fz="sm">
            <Table.Tbody>
              {data.worstParameters.map((p) => (
                <Table.Tr key={p.name}>
                  <Table.Td>
                    <Text size="sm" fw={500}>{p.name}</Text>
                    {p.worstModels.length > 0 && <Text size="xs" c="dimmed">хуже всего: {p.worstModels.join(', ')}</Text>}
                  </Table.Td>
                  <Table.Td c="dimmed" ta="right">моделей {p.models}</Table.Td>
                  <Table.Td ta="right"><Share value={p.fillRate} /></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
      </SimpleGrid>
    </Stack>
  );
}
