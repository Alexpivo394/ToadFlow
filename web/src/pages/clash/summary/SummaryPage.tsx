import { useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { Anchor, Autocomplete, Group, ScrollArea, SegmentedControl, Select, SimpleGrid, Stack, Table, Text, TextInput } from '@mantine/core';
import { BarChart, LineChart } from '@mantine/charts';
import { useApi } from '../../../api/queries';
import type { CheckView, ClashAnalyticsView } from '../../../api/types';
import { Empty, PageTitle, Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { Stat } from '../../analytics/parts';

type Period = 'week' | 'month' | 'custom';
type GroupBy = 'discipline' | 'object' | 'part';

const noProject = 'Без проекта';
const n = (value: number) => value.toLocaleString('ru-RU');

/** Дата в форме yyyy-MM-dd по местному времени. */
function isoDay(date: Date): string {
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function daysAgo(days: number): string {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return isoDay(date);
}

/** Куда ведёт строка «Кто отстаёт»: в «Проверки» с фильтром по разделу или проекту, проверка — на свою страницу. */
function laggingLink(groupBy: GroupBy, key: string, partId: string | null): string {
  if (groupBy === 'part' && partId) return `/clash/checks/${partId}`;
  return `/clash/checks?${new URLSearchParams(groupBy === 'object' ? { object: key } : { discipline: key })}`;
}

/**
 * «Коллизии → Сводка» в стиле Аналитики LOI: сколько активных, сколько добавилось и исправилось за период, доля фиктивных,
 * возраст и «кто правит», динамика открытых и «Кто отстаёт» по разделам, проектам или проверкам.
 */
export function SummaryPage() {
  const [project, setProject] = useState<string | null>(null);
  const [partId, setPartId] = useState<string | null>(null);
  const [discipline, setDiscipline] = useState('');
  const [period, setPeriod] = useState<Period>('month');
  const [custom, setCustom] = useState({ from: daysAgo(29), to: daysAgo(0) });
  const [groupBy, setGroupBy] = useState<GroupBy>('discipline');
  const checks = useApi<CheckView[]>(['clash-checks'], '/api/clash/checks');

  const range = period === 'week' ? { from: daysAgo(6), to: daysAgo(0) } : period === 'month' ? { from: daysAgo(29), to: daysAgo(0) } : custom;
  const query = new URLSearchParams({ from: range.from, to: range.to, groupBy });
  if (project) query.set('object', project);
  if (partId) query.set('partId', partId);
  if (discipline.trim()) query.set('discipline', discipline.trim());
  const view = useApi<ClashAnalyticsView>(['clash-analytics', query.toString()], `/api/clash/analytics?${query}`);
  const v = view.data;

  const projects = [...new Set((checks.data ?? []).map((c) => c.object ?? noProject))];
  const parts = (checks.data ?? []).filter((c) => !project || (c.object ?? noProject) === project);
  const disciplines = [...new Set((checks.data ?? []).flatMap((c) => c.models.map((m) => m.discipline).filter((d): d is string => !!d)))].sort();

  return (
    <>
      <PageTitle>{t.nav.clashSummary}</PageTitle>
      <Group gap="xs" mb="md" wrap="wrap" align="flex-end">
        <Select size="xs" w={160} label="Проект" clearable data={projects} value={project} onChange={(value) => { setProject(value); setPartId(null); }} />
        <Select size="xs" w={200} label="Проверка" clearable data={parts.map((c) => ({ value: c.partId, label: c.name }))} value={partId} onChange={setPartId} />
        <Autocomplete size="xs" w={140} label="Раздел" data={disciplines} value={discipline} onChange={setDiscipline} />
        <Stack gap={2}>
          <Text size="xs" fw={500}>Период</Text>
          <SegmentedControl size="xs" value={period} onChange={(value) => setPeriod(value as Period)}
            data={[{ value: 'week', label: 'Неделя' }, { value: 'month', label: 'Месяц' }, { value: 'custom', label: 'Свой' }]} />
        </Stack>
        {period === 'custom' && (
          <>
            <TextInput size="xs" type="date" label="С" value={custom.from} onChange={(e) => setCustom({ ...custom, from: e.currentTarget.value })} />
            <TextInput size="xs" type="date" label="По" value={custom.to} onChange={(e) => setCustom({ ...custom, to: e.currentTarget.value })} />
          </>
        )}
      </Group>

      {v && (
        <Stack gap="md">
          <SimpleGrid cols={{ base: 2, md: 5 }}>
            <Stat title="Активных" value={n(v.current.open)} hint="открытых сейчас" />
            <Stat title="Добавилось" value={n(v.period.new)} hint="за период" />
            <Stat title="Исправлено" value={n(v.period.fixed)} hint="за период" />
            <Stat title="Вернулось" value={n(v.period.returned)} hint="за период" />
            <Stat title="Доля фиктивных" value={`${Math.round(v.current.fakeShare * 100)} %`} hint={`${n(v.current.fake)} фиктивных`} />
          </SimpleGrid>

          <Panel title="Открытые по дням">
            <LineChart h={220} data={v.series.map((p) => ({ date: p.date.slice(8, 10) + '.' + p.date.slice(5, 7), Открытые: p.open }))}
              dataKey="date" series={[{ name: 'Открытые', color: 'orange.6' }]} curveType="linear" withDots={false} tickLine="none" gridAxis="y" />
          </Panel>

          <SimpleGrid cols={{ base: 1, md: 2 }}>
            <Panel title="Возраст открытых">
              <BarChart h={180} data={v.current.ageBuckets.map((b) => ({ bucket: b.label, Коллизий: b.count }))} dataKey="bucket"
                series={[{ name: 'Коллизий', color: 'blue.6' }]} tickLine="none" gridAxis="none" />
            </Panel>
            <Panel title="На ком коллизии">
              <Text size="sm" mb={4} c={v.current.undecided > 0 ? 'orange' : undefined}>
                Не решено, кто правит: {n(v.current.undecided)} из {n(v.current.open)}
              </Text>
              <Text size="xs" c="dimmed" mb="xs">Пока «кто правит» — обе стороны, коллизия висит на двух разделах. Решают «Беру» и «Передать…» в карточке.</Text>
              {v.current.byDiscipline.length === 0 ? <Empty>Открытых нет</Empty> : (
                <ScrollArea.Autosize mah={200}>
                  <Table aria-label="На ком коллизии" highlightOnHover>
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th>Раздел</Table.Th>
                        <Table.Th ta="right">Правит сам</Table.Th>
                        <Table.Th ta="right">Не решено</Table.Th>
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {v.current.byDiscipline.map((d) => (
                        <Table.Tr key={d.discipline}>
                          <Table.Td>{d.discipline || 'без раздела'}</Table.Td>
                          <Table.Td ta="right">{n(d.assigned)}</Table.Td>
                          <Table.Td ta="right" c={d.undecided > 0 ? 'orange' : undefined}>{n(d.undecided)}</Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </ScrollArea.Autosize>
              )}
            </Panel>
          </SimpleGrid>

          <Panel title="Кто отстаёт" actions={
            <SegmentedControl size="xs" value={groupBy} onChange={(value) => setGroupBy(value as GroupBy)}
              data={[{ value: 'discipline', label: 'Разделы' }, { value: 'object', label: 'Проекты' }, { value: 'part', label: 'Проверки' }]} />
          }>
            {v.lagging.length === 0 ? <Empty>Открытых коллизий нет</Empty> : (
              <Table highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{groupBy === 'discipline' ? 'Раздел' : groupBy === 'object' ? 'Проект' : 'Проверка'}</Table.Th>
                    <Table.Th ta="right">Активных</Table.Th>
                    <Table.Th ta="right">Рост за период</Table.Th>
                    <Table.Th ta="right">Висят</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {v.lagging.map((l) => (
                    <Table.Tr key={`${l.key}-${l.partId ?? ''}`}>
                      <Table.Td><Anchor component={RouterLink} to={laggingLink(groupBy, l.key, l.partId)} size="sm">{l.key}</Anchor></Table.Td>
                      <Table.Td ta="right">{n(l.open)}</Table.Td>
                      <Table.Td ta="right" c={l.growth > 0 ? 'red' : l.growth < 0 ? 'green' : undefined}>{l.growth > 0 ? `+${n(l.growth)}` : n(l.growth)}</Table.Td>
                      <Table.Td ta="right">{n(l.stuck)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Panel>
        </Stack>
      )}
    </>
  );
}
