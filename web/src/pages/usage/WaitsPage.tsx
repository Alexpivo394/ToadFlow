import { useState } from 'react';
import { Drawer, Group, Select, Stack, Tabs, Text } from '@mantine/core';
import { useSearchParams } from 'react-router';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryState } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import type { WaitGroup, WaitRow, WaitsView } from './api';
import { EmptyState } from './EmptyState';
import { FilterBar } from './FilterBar';
import { formatDuration, formatHours } from './format';
import { Block, CsvButton, MachineLink, PersonLink, Trend } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';
import { WaitCases } from './WaitCases';

const groups: WaitGroup[] = ['model', 'machine', 'user', 'server', 'link'];
const kinds = ['Open', 'Sync', 'Save', 'Reload'];

/**
 * «Ожидание»: сколько люди ждут Revit — по моделям, машинам, людям, серверам Revit и связям. Медиана и 90-й процентиль — по
 * успешным операциям; строка открывает отдельные случаи.
 */
export function WaitsPage() {
  const [params, setParams] = useSearchParams();
  const tab = (groups as string[]).includes(params.get('tab') ?? '') ? (params.get('tab') as WaitGroup) : 'model';
  const kind = tab === 'link' ? undefined : params.get('kind') ?? undefined;
  const { query } = useUsageFilter();
  const path = withQuery('/api/usage/waits', query, { group: tab, kind });
  const waits = useApi<WaitsView>(['usage', 'waits', path], path, { keepPrevious: true });
  const [open, setOpen] = useState<WaitRow | null>(null);

  const setParam = (name: string, value: string | null) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (value) next.set(name, value);
      else next.delete(name);
      next.delete('page');
      return next;
    }, { replace: true });

  const title = (row: WaitRow) =>
    tab === 'user' ? <PersonLink id={row.key} name={row.title} />
      : tab === 'machine' ? <MachineLink machineKey={row.key} name={row.title} />
        : <Text size="sm" title={row.key}>{row.title}</Text>;

  const columns: Column<WaitRow>[] = [
    { key: 'title', title: t.usage.title, render: title, sort: (r) => r.title },
    { key: 'count', title: t.usage.count, render: (r) => r.count, sort: (r) => r.count },
    { key: 'median', title: t.usage.median, render: (r) => formatDuration(r.medianMs), sort: (r) => r.medianMs },
    { key: 'p90', title: t.usage.p90, render: (r) => formatDuration(r.p90Ms), sort: (r) => r.p90Ms },
    { key: 'total', title: t.usage.total, render: (r) => formatHours(r.totalMs), sort: (r) => r.totalMs },
    { key: 'failed', title: t.usage.failed, render: (r) => r.failed || '—', sort: (r) => r.failed },
    { key: 'trend', title: t.usage.trend, render: (r) => <Trend values={r.trend} format={(v) => formatDuration(v)} /> },
  ];

  const csv = (rows: WaitRow[]) => () => rows.map((r) => [r.title, r.count, seconds(r.medianMs), seconds(r.p90Ms), hours(r.totalMs), r.failed]);

  return (
    <Stack gap="md">
      <FilterBar basis={waits.data?.basis} />
      <Tabs value={tab} onChange={(value) => setParam('tab', value === 'model' ? null : value)}>
        <Tabs.List>
          {groups.map((group) => <Tabs.Tab key={group} value={group}>{t.usage.groups[group]}</Tabs.Tab>)}
        </Tabs.List>
      </Tabs>
      <QueryState query={waits}>
        {(view) => view.rows.length === 0 ? (
          tab === 'link' ? <Block><Text size="sm">{t.usage.linksEmpty}</Text></Block> : <EmptyState />
        ) : (
          <Block
            actions={(
              <Group gap="sm">
                {tab !== 'link' && (
                  <Select size="xs" w={220} aria-label={t.usage.kind} placeholder={t.usage.allKinds} clearable value={kind ?? null}
                    data={kinds.map((k) => ({ value: k, label: t.usage.kinds[k] }))} onChange={(value) => setParam('kind', value)} />
                )}
                <CsvButton fileName={`ожидание-${t.usage.groups[tab]}`}
                  header={[t.usage.title, t.usage.count, `${t.usage.median}, с`, `${t.usage.p90}, с`, `${t.usage.total}, ч`, t.usage.failed]}
                  rows={csv(view.rows)} />
              </Group>
            )}>
            <DataTable rows={view.rows} columns={columns} rowKey={(r) => r.key} search={(r) => r.title} onRowClick={setOpen} />
          </Block>
        )}
      </QueryState>
      <Drawer opened={open !== null} onClose={() => setOpen(null)} position="right" size="xl" title={open ? `${t.usage.cases}: ${open.title}` : ''}>
        {open && <WaitCases group={tab} rowKey={open.key} kind={kind} query={query} />}
      </Drawer>
    </Stack>
  );
}

const seconds = (ms: number | null) => (ms == null ? null : Math.round(ms / 1000));
const hours = (ms: number) => Math.round(ms / 360_000) / 10;
