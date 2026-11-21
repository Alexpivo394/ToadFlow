import { useState } from 'react';
import { BarChart } from '@mantine/charts';
import { Button, Group, SegmentedControl, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { IconDownload } from '@tabler/icons-react';
import { downloadFile } from '../../api/client';
import { useApi } from '../../api/queries';
import { QueryError } from '../../components/QueryState';
import { PageTitle, Panel } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey } from './api';
import type { MissRow } from './MissesPage';

interface CardCount {
  cardId: string;
  name: string;
  section: string;
  loads: number;
}

interface FamiliesAnalytics {
  weeks: { week: string; created: number; closed: number }[];
  byDirection: { name: string; open: number; overdue: number; accepted: number; medianHours: number | null; declinedShare: number }[];
  byAssignee: { name: string; open: number }[];
  topLoaded: CardCount[];
  unused: CardCount[];
  topMisses: MissRow[];
}

const hours = (value: number | null) => (value === null ? '—' : value >= 48 ? `${Math.round(value / 24)} дн.` : `${Math.round(value)} ч`);

function Simple({ head, rows }: { head: string[]; rows: (string | number)[][] }) {
  if (rows.length === 0) return <Text size="sm" c="dimmed">{t.common.empty}</Text>;
  return (
    <Table striped withTableBorder>
      <Table.Thead><Table.Tr>{head.map((h) => <Table.Th key={h}>{h}</Table.Th>)}</Table.Tr></Table.Thead>
      <Table.Tbody>
        {rows.map((row, i) => <Table.Tr key={i}>{row.map((cell, j) => <Table.Td key={j}>{cell}</Table.Td>)}</Table.Tr>)}
      </Table.Tbody>
    </Table>
  );
}

/** Аналитика семейств для руководителя: заявки по неделям, сроки и возвраты по направлениям, нагрузка, востребованность каталога. */
export function AnalyticsPage() {
  const [days, setDays] = useState('90');
  const analytics = useApi<FamiliesAnalytics>([familiesKey, 'analytics', days], `/api/families/analytics?days=${days}`);
  const a = analytics.data;
  return (
    <Stack gap="md">
      <PageTitle actions={
        <Group>
          <SegmentedControl size="xs" value={days} onChange={setDays} data={[{ value: '30', label: '30 дней' }, { value: '90', label: '90 дней' }, { value: '365', label: 'год' }]} />
          <Button size="xs" variant="default" leftSection={<IconDownload size={14} />}
            onClick={() => void downloadFile(`/api/families/analytics.xlsx?days=${days}`, 'Аналитика семейств.xlsx')}>{t.insights.excel}</Button>
        </Group>
      }>{t.insights.analyticsTitle}</PageTitle>
      <QueryError query={analytics} />
      {a && (
        <>
          <Panel title={t.insights.weeks}>
            <BarChart h={220} data={a.weeks.map((w) => ({ week: w.week.slice(5).split('-').reverse().join('.'), [t.insights.created]: w.created, [t.insights.closed]: w.closed }))}
              dataKey="week" series={[{ name: t.insights.created, color: 'blue.5' }, { name: t.insights.closed, color: 'teal.5' }]} />
          </Panel>
          <Panel title={t.insights.byDirection}>
            <Simple head={[t.requests.direction, t.insights.open, t.insights.overdue, t.insights.accepted, t.insights.median, t.insights.declined]}
              rows={a.byDirection.map((d) => [d.name, d.open, d.overdue, d.accepted, hours(d.medianHours), `${Math.round(d.declinedShare * 100)}%`])} />
          </Panel>
          <SimpleGrid cols={{ base: 1, md: 2 }}>
            <Panel title={t.insights.load}>
              <Simple head={[t.requests.assignee, t.insights.open]} rows={a.byAssignee.map((x) => [x.name, x.open])} />
            </Panel>
            <Panel title={t.insights.topMisses}>
              <Simple head={[t.insights.query, t.insights.times]} rows={a.topMisses.map((m) => [m.query, m.count])} />
            </Panel>
            <Panel title={t.insights.topLoaded}>
              <Simple head={[t.insights.family, t.families.section, t.insights.loads]} rows={a.topLoaded.map((c) => [c.name, c.section, c.loads])} />
            </Panel>
            <Panel title={t.insights.unused}>
              <Simple head={[t.insights.family, t.families.section]} rows={a.unused.map((c) => [c.name, c.section])} />
            </Panel>
          </SimpleGrid>
        </>
      )}
    </Stack>
  );
}
