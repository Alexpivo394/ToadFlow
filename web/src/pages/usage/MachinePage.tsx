import { LineChart } from '@mantine/charts';
import { Alert, Anchor, Breadcrumbs, Group, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { Link, useParams } from 'react-router';
import { useApi } from '../../api/queries';
import { QueryState } from '../../components/QueryState';
import { formatDateTime } from '../../lib/format';
import { t } from '../../i18n/ru';
import type { MachineCard } from './api';
import { FilterBar } from './FilterBar';
import { formatDuration, formatMemory } from './format';
import { Block, PersonLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

/** Карточка машины: железо, кто на ней работает, ожидание по неделям и аварийные сессии. */
export function MachinePage() {
  const { key = '' } = useParams();
  const { query } = useUsageFilter();
  const path = withQuery(`/api/usage/machines/${encodeURIComponent(key)}`, query);
  const card = useApi<MachineCard>(['usage', 'machine', path], path, { keepPrevious: true });

  return (
    <Stack gap="md">
      <Breadcrumbs>
        <Anchor component={Link} to="/usage/machines" size="sm">{t.usage.machines}</Anchor>
        <Text size="sm">{card.data?.name ?? key}</Text>
      </Breadcrumbs>
      <FilterBar />
      <QueryState query={card} title={t.usage.machineMissing}>
        {(m) => (
          <Stack gap="md">
            <Title order={2}>{m.name}</Title>
            <SimpleGrid cols={{ base: 1, md: 2 }}>
              <Block>
                <Table fz="sm" withRowBorders={false}>
                  <Table.Tbody>
                    <Fact label={t.usage.cpu} value={`${m.cpu ?? '—'}${m.cores ? ` · ${m.cores} ядер` : ''}`} />
                    <Fact label={t.usage.ram} value={formatMemory(m.ramMb)} />
                    <Fact label={t.usage.gpu} value={`${m.gpu ?? '—'}${m.gpuMemoryMb ? ` · ${formatMemory(m.gpuMemoryMb)}` : ''}`} />
                    <Fact label={t.usage.disk}
                      value={`${t.usage.diskKinds[m.diskKind]}${m.diskTotalGb ? ` · ${m.diskTotalGb} ГБ, свободно ${m.diskFreeGb ?? '—'} ГБ` : ''}`} />
                    <Fact label={t.usage.os} value={m.os ?? '—'} />
                    <Fact label={t.usage.revitYears} value={m.revitYears.join(', ') || '—'} />
                    <Fact label={t.usage.toadTools} value={m.toadTools ?? '—'} />
                    <Fact label={t.usage.lastSeen} value={formatDateTime(m.lastSeenAt)} />
                  </Table.Tbody>
                </Table>
              </Block>
              <Block title={t.usage.whoWorks}>
                <Group gap="xl">
                  <Text size="sm">{t.usage.openMedian}: <b>{formatDuration(m.openMedianMs)}</b></Text>
                  <Text size="sm">{t.usage.syncMedian}: <b>{formatDuration(m.syncMedianMs)}</b></Text>
                  <Text size="sm">{t.usage.activeHours}: <b>{m.activeHours.toLocaleString('ru-RU')}</b></Text>
                </Group>
                {m.users.length === 0 ? <Text size="sm" c="dimmed">—</Text> : (
                  <Table fz="sm">
                    <Table.Thead><Table.Tr><Table.Th>{t.usage.person}</Table.Th><Table.Th>{t.usage.sessions}</Table.Th></Table.Tr></Table.Thead>
                    <Table.Tbody>
                      {m.users.map((u) => (
                        <Table.Tr key={u.id}><Table.Td><PersonLink id={u.id} name={u.name} /></Table.Td><Table.Td>{u.sessions}</Table.Td></Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                )}
              </Block>
            </SimpleGrid>
            <Block title={t.usage.weeks}>
              <LineChart h={220} data={m.weeks.map((w) => ({ week: w.start.slice(5).split('-').reverse().join('.'), open: sec(w.openMedianMs), sync: sec(w.syncMedianMs) }))}
                dataKey="week" withLegend connectNulls={false} unit=" с" strokeWidth={2}
                series={[{ name: 'open', label: t.usage.openMedian, color: 'blue.6' }, { name: 'sync', label: t.usage.syncMedian, color: 'orange.6' }]} />
            </Block>
            <Block title={`${t.usage.abnormalSessions}: ${m.abnormalSessions.length}`}>
              <Alert variant="light" color="gray">{t.usage.abnormalNote}</Alert>
              {m.abnormalSessions.length > 0 && (
                <Table fz="sm">
                  <Table.Thead>
                    <Table.Tr><Table.Th>{t.usage.when}</Table.Th><Table.Th>{t.usage.sessionEnd}</Table.Th><Table.Th>{t.usage.person}</Table.Th></Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {m.abnormalSessions.map((s) => (
                      <Table.Tr key={s.id}>
                        <Table.Td>{formatDateTime(s.startedAt)}</Table.Td>
                        <Table.Td>{formatDateTime(s.seenAt)}</Table.Td>
                        <Table.Td><PersonLink id={s.user.id} name={s.user.name} /></Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Block>
          </Stack>
        )}
      </QueryState>
    </Stack>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <Table.Tr>
      <Table.Td c="dimmed" w={170}>{label}</Table.Td>
      <Table.Td>{value}</Table.Td>
    </Table.Tr>
  );
}

const sec = (ms: number | null) => (ms == null ? null : Math.round(ms / 1000));
