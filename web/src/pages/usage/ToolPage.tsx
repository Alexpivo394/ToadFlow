import { BarChart } from '@mantine/charts';
import { Anchor, Breadcrumbs, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { Link, useParams } from 'react-router';
import { useApi } from '../../api/queries';
import { QueryState } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import type { CommandCard } from './api';
import { FilterBar } from './FilterBar';
import { Block, PersonLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

/** Карточка команды: запуски по неделям и кто ей пользуется. */
export function ToolPage() {
  const { source = '', commandId = '' } = useParams();
  const { query } = useUsageFilter();
  const path = withQuery(`/api/usage/commands/${encodeURIComponent(source)}/${encodeURIComponent(commandId)}`, query);
  const card = useApi<CommandCard>(['usage', 'command', path], path, { keepPrevious: true });

  return (
    <Stack gap="md">
      <Breadcrumbs>
        <Anchor component={Link} to="/usage/tools" size="sm">{t.usage.tools}</Anchor>
        <Text size="sm">{card.data?.name ?? commandId}</Text>
      </Breadcrumbs>
      <FilterBar />
      <QueryState query={card} title={t.usage.commandMissing}>
        {(c) => (
          <Stack gap="md">
            <Title order={2}>{c.name}</Title>
            <SimpleGrid cols={{ base: 1, md: 2 }}>
              <Block title={t.usage.commandWeeks}>
                <BarChart h={220} data={c.weeks.map((w) => ({ week: w.start.slice(5).split('-').reverse().join('.'), count: w.count }))} dataKey="week"
                  series={[{ name: 'count', label: t.usage.runs, color: 'teal.6' }]} barProps={{ radius: [4, 4, 0, 0] }} />
              </Block>
              <Block title={t.usage.whoUses}>
                <Table fz="sm">
                  <Table.Thead><Table.Tr><Table.Th>{t.usage.person}</Table.Th><Table.Th>{t.usage.runs}</Table.Th></Table.Tr></Table.Thead>
                  <Table.Tbody>
                    {c.users.map((u) => (
                      <Table.Tr key={u.id}><Table.Td><PersonLink id={u.id} name={u.name} /></Table.Td><Table.Td>{u.count}</Table.Td></Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Block>
            </SimpleGrid>
          </Stack>
        )}
      </QueryState>
    </Stack>
  );
}
