import { BarChart } from '@mantine/charts';
import { Anchor, Breadcrumbs, Group, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { Link, useParams } from 'react-router';
import { useApi } from '../../api/queries';
import { QueryState } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import type { PersonView } from './api';
import { FilterBar } from './FilterBar';
import { formatDuration, formatHours } from './format';
import { Block, MachineLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

/** Карточка человека: машины, активное время по дням, ожидания, команды, веб и плагины. */
export function PersonPage() {
  const { id = '' } = useParams();
  const { query } = useUsageFilter();
  const path = withQuery(`/api/usage/people/${encodeURIComponent(id)}`, query);
  const person = useApi<PersonView>(['usage', 'person', path], path, { keepPrevious: true });

  return (
    <Stack gap="md">
      <Breadcrumbs>
        <Anchor component={Link} to="/usage/coverage" size="sm">{t.usage.coverage}</Anchor>
        <Text size="sm">{person.data?.user.name ?? ''}</Text>
      </Breadcrumbs>
      <FilterBar />
      <QueryState query={person} title={t.usage.personMissing}>
        {(p) => (
          <Stack gap="md">
            <div>
              <Title order={2}>{p.user.name}</Title>
              <Text size="sm" c="dimmed">
                {[t.role[p.user.role as keyof typeof t.role] ?? p.user.role, p.user.position, p.user.disciplines.join(', ')].filter(Boolean).join(' · ')}
              </Text>
            </div>
            <Block title={t.usage.personMachines}>
              {p.machines.length === 0 ? <Text size="sm" c="dimmed">—</Text> : (
                <Group gap="md">{p.machines.map((m) => <MachineLink key={m.key} machineKey={m.key} name={m.name} />)}</Group>
              )}
            </Block>
            <Block title={`${t.usage.activity}, ч`}>
              <BarChart h={200} dataKey="day" series={[{ name: 'hours', label: t.usage.activeHours, color: 'teal.6' }]} barProps={{ radius: [4, 4, 0, 0] }}
                data={p.days.map((d) => ({ day: d.day.slice(5).split('-').reverse().join('.'), hours: Math.round(d.activeSeconds / 360) / 10 }))} />
            </Block>
            <SimpleGrid cols={{ base: 1, md: 2 }}>
              <Block title={`${t.usage.personWaits}: ${formatHours(p.waits.totalMs)}`}>
                <Table fz="sm">
                  <Table.Thead><Table.Tr><Table.Th>{t.usage.kind}</Table.Th><Table.Th>{t.usage.count}</Table.Th><Table.Th>{t.usage.median}</Table.Th></Table.Tr></Table.Thead>
                  <Table.Tbody>
                    {p.waits.rows.map((r) => (
                      <Table.Tr key={r.kind}><Table.Td>{t.usage.kinds[r.kind]}</Table.Td><Table.Td>{r.count}</Table.Td><Table.Td>{formatDuration(r.medianMs)}</Table.Td></Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Block>
              <Block title={t.usage.personCommands}>
                <Table fz="sm">
                  <Table.Thead><Table.Tr><Table.Th>{t.usage.title}</Table.Th><Table.Th>{t.usage.source}</Table.Th><Table.Th>{t.usage.runs}</Table.Th></Table.Tr></Table.Thead>
                  <Table.Tbody>
                    {p.commands.map((c) => (
                      <Table.Tr key={`${c.source}/${c.commandId}`}>
                        <Table.Td><Anchor component={Link} to={`/usage/tools/${c.source}/${encodeURIComponent(c.commandId)}`} size="sm">{c.name}</Anchor></Table.Td>
                        <Table.Td>{t.usage.sources[c.source]}</Table.Td>
                        <Table.Td>{c.count}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Block>
              <Block title={t.usage.personWeb}>
                {p.web.length === 0 ? <Text size="sm" c="dimmed">—</Text> : p.web.map((w) => <Text key={w.space} size="sm">{`${w.space}: ${w.visits}`}</Text>)}
              </Block>
              <Block title={t.usage.personPlugins}>
                {p.plugins.length === 0 ? <Text size="sm" c="dimmed">—</Text> : p.plugins.map((a) => <Text key={a.area} size="sm">{`${a.area}: ${a.calls}`}</Text>)}
              </Block>
            </SimpleGrid>
          </Stack>
        )}
      </QueryState>
    </Stack>
  );
}
