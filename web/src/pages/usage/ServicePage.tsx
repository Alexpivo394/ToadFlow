import { BarChart } from '@mantine/charts';
import { SimpleGrid, Stack, Table, Tabs, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryState } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import type { ServiceView } from './api';
import { EmptyState } from './EmptyState';
import { FilterBar } from './FilterBar';
import { Block, CsvButton, PersonLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

type PageRow = ServiceView['web']['pages'][number];

/** «Сервис»: кто и как пользуется вебом ToadFlow и плагинами — по дням, пространствам, страницам, ролям и разделам API. */
export function ServicePage() {
  const { query } = useUsageFilter();
  const path = withQuery('/api/usage/service', query);
  const service = useApi<ServiceView>(['usage', 'service', path], path, { keepPrevious: true });

  const pageColumns: Column<PageRow>[] = [
    { key: 'space', title: t.usage.space, render: (r) => r.space, sort: (r) => r.space },
    { key: 'page', title: t.usage.page, render: (r) => r.page, sort: (r) => r.page },
    { key: 'visits', title: t.usage.visits, render: (r) => r.visits, sort: (r) => r.visits },
    { key: 'people', title: t.usage.people, render: (r) => r.people, sort: (r) => r.people },
  ];

  return (
    <Stack gap="md">
      <FilterBar basis={service.data?.basis} />
      <QueryState query={service}>
        {(view) => view.web.days.length === 0 && view.plugins.areas.length === 0 ? <EmptyState /> : (
          <Tabs defaultValue="web">
            <Tabs.List mb="md">
              <Tabs.Tab value="web">{t.usage.web}</Tabs.Tab>
              <Tabs.Tab value="plugins">{t.usage.plugins}</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="web">
              <Stack gap="md">
                <Block title={t.usage.webDays}>
                  <BarChart h={200} data={view.web.days.map((d) => ({ day: d.day.slice(5).split('-').reverse().join('.'), people: d.people }))} dataKey="day"
                    series={[{ name: 'people', label: t.usage.people, color: 'teal.6' }]} barProps={{ radius: [4, 4, 0, 0] }} />
                </Block>
                <SimpleGrid cols={{ base: 1, md: 2 }}>
                  <Block title={t.usage.spaces}>
                    <SmallTable header={[t.usage.space, t.usage.visits, t.usage.people]}
                      rows={view.web.spaces.map((s) => [s.space, s.visits, s.people])} />
                  </Block>
                  <Block title={t.usage.roles}>
                    <SmallTable header={[t.usage.role, t.usage.people, t.usage.visits]}
                      rows={view.web.roles.map((r) => [t.role[r.role as keyof typeof t.role] ?? r.role, r.people, r.visits])} />
                  </Block>
                </SimpleGrid>
                <Block title={t.usage.pages} actions={(
                  <CsvButton fileName="сервис-страницы" header={[t.usage.space, t.usage.page, t.usage.visits, t.usage.people]}
                    rows={() => view.web.pages.map((p) => [p.space, p.page, p.visits, p.people])} />
                )}>
                  <DataTable rows={view.web.pages} columns={pageColumns} rowKey={(r) => `${r.space} ${r.page}`} search={(r) => r.page} urlKey="pages" />
                </Block>
              </Stack>
            </Tabs.Panel>
            <Tabs.Panel value="plugins">
              <SimpleGrid cols={{ base: 1, md: 2 }}>
                <Block title={t.usage.area}>
                  <SmallTable header={[t.usage.area, t.usage.calls, t.usage.people]} rows={view.plugins.areas.map((a) => [a.area, a.calls, a.people])} />
                </Block>
                <Block title={t.usage.people} actions={(
                  <CsvButton fileName="сервис-плагины" header={[t.usage.person, t.usage.area, t.usage.calls]}
                    rows={() => view.plugins.users.flatMap((u) => u.areas.map((a) => [u.name, a.area, a.calls]))} />
                )}>
                  <Table fz="sm">
                    <Table.Thead><Table.Tr><Table.Th>{t.usage.person}</Table.Th><Table.Th>{t.usage.calls}</Table.Th></Table.Tr></Table.Thead>
                    <Table.Tbody>
                      {view.plugins.users.map((u) => (
                        <Table.Tr key={u.id}>
                          <Table.Td><PersonLink id={u.id} name={u.name} /></Table.Td>
                          <Table.Td><Text size="sm">{u.areas.map((a) => `${a.area}: ${a.calls}`).join(', ')}</Text></Table.Td>
                        </Table.Tr>
                      ))}
                    </Table.Tbody>
                  </Table>
                </Block>
              </SimpleGrid>
            </Tabs.Panel>
          </Tabs>
        )}
      </QueryState>
    </Stack>
  );
}

function SmallTable({ header, rows }: { header: string[]; rows: (string | number)[][] }) {
  if (rows.length === 0) return <Text size="sm" c="dimmed">—</Text>;
  return (
    <Table fz="sm">
      <Table.Thead><Table.Tr>{header.map((h) => <Table.Th key={h}>{h}</Table.Th>)}</Table.Tr></Table.Thead>
      <Table.Tbody>
        {rows.map((row) => <Table.Tr key={String(row[0])}>{row.map((cell, i) => <Table.Td key={i}>{cell}</Table.Td>)}</Table.Tr>)}
      </Table.Tbody>
    </Table>
  );
}
