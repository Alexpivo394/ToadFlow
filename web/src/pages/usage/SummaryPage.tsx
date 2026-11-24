import { BarChart } from '@mantine/charts';
import { Anchor, Group, Paper, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { Link } from 'react-router';
import { useApi } from '../../api/queries';
import { QueryState } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import type { SummaryTotals, SummaryView } from './api';
import { EmptyState } from './EmptyState';
import { FilterBar } from './FilterBar';
import { formatChange, formatDuration, formatHours } from './format';
import { Block, MachineLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

/** Слои графика ожидания: порядок и цвета постоянны (проверены валидатором палитры). */
const layers = [
  { name: 'open', label: t.usage.kinds.Open, color: 'blue.6' },
  { name: 'link', label: t.usage.kinds.Link, color: 'orange.6' },
  { name: 'sync', label: t.usage.kinds.Sync, color: 'cyan.6' },
  { name: 'save', label: t.usage.kinds.Save, color: 'grape.6' },
];

const hours = (ms: number) => Math.round(ms / 360_000) / 10;

/** «Обзор» аналитики: четыре итога к прошлому периоду, ожидание по дням слоями и «Что болит». */
export function SummaryPage() {
  const { query } = useUsageFilter();
  const path = withQuery('/api/usage/summary', query);
  const summary = useApi<SummaryView>(['usage', 'summary', path], path, { keepPrevious: true });

  return (
    <Stack gap="md">
      <FilterBar basis={summary.data?.basis} />
      <QueryState query={summary}>
        {(view) => isEmpty(view.totals) && view.basis.people === 0 ? <EmptyState /> : (
          <Stack gap="md">
            <SimpleGrid cols={{ base: 2, md: 4 }}>
              <Tile value={formatHours(view.totals.waitMs)} label={t.usage.tiles.waitHours} change={formatChange(view.totals.waitMs, view.previous.waitMs)} />
              <Tile value={String(view.totals.abnormalSessions)} label={t.usage.tiles.abnormal}
                change={formatChange(view.totals.abnormalSessions, view.previous.abnormalSessions)} />
              <Tile value={String(view.totals.people)} label={t.usage.tiles.people} change={formatChange(view.totals.people, view.previous.people)} />
              <Tile value={String(view.totals.toadToolsRuns)} label={t.usage.tiles.runs} change={formatChange(view.totals.toadToolsRuns, view.previous.toadToolsRuns)} />
            </SimpleGrid>
            <Block title={`${t.usage.waitByDay}, ч`}>
              <BarChart h={260} type="stacked" withLegend dataKey="day" series={layers} valueFormatter={(v) => `${v.toLocaleString('ru-RU')} ч`}
                data={view.days.map((d) => ({
                  day: d.day.slice(5).split('-').reverse().join('.'), open: hours(d.openMs), link: hours(d.linkMs), sync: hours(d.syncMs), save: hours(d.saveMs),
                }))} />
            </Block>
            <Block title={t.usage.hurts}>
              <SimpleGrid cols={{ base: 1, md: 3 }}>
                <Hurt title={t.usage.slowModels} link="/usage/waits" rows={view.slowModels.map((m) => ({
                  key: m.key, name: <Text size="sm" title={m.key}>{m.title}</Text>, value: formatDuration(m.medianMs),
                }))} />
                <Hurt title={t.usage.slowMachines} link="/usage/waits?tab=machine" rows={view.slowMachines.map((m) => ({
                  key: m.key, name: <MachineLink machineKey={m.key} name={m.name} />, value: formatHours(m.totalMs),
                }))} />
                <Hurt title={t.usage.crashingMachines} link="/usage/machines" rows={view.crashingMachines.map((m) => ({
                  key: m.key, name: <MachineLink machineKey={m.key} name={m.name} />, value: String(m.abnormal),
                }))} />
              </SimpleGrid>
            </Block>
          </Stack>
        )}
      </QueryState>
    </Stack>
  );
}

const isEmpty = (totals: SummaryTotals) => !totals.waitMs && !totals.abnormalSessions && !totals.people && !totals.toadToolsRuns;

function Tile({ value, label, change }: { value: string; label: string; change: string | null }) {
  return (
    <Paper withBorder radius="md" p="md">
      <Text fz={28} fw={700} lh={1.1}>{value}</Text>
      <Text size="sm" c="dimmed">{label}</Text>
      {change && <Text size="xs" c="dimmed" mt={4}>{`${change} ${t.usage.vsPrevious}`}</Text>}
    </Paper>
  );
}

function Hurt({ title, link, rows }: { title: string; link: string; rows: { key: string; name: React.ReactNode; value: string }[] }) {
  return (
    <Stack gap={6}>
      <Group justify="space-between">
        <Anchor component={Link} to={link} size="sm" fw={600}>{title}</Anchor>
      </Group>
      {rows.length === 0 ? <Text size="sm" c="dimmed">{t.usage.nothingHurts}</Text> : (
        <Table fz="sm" withRowBorders={false}>
          <Table.Tbody>
            {rows.map((r) => (
              <Table.Tr key={r.key}><Table.Td>{r.name}</Table.Td><Table.Td ta="right">{r.value}</Table.Td></Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  );
}
