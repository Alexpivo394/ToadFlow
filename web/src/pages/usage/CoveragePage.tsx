import { useState } from 'react';
import { Badge, Checkbox, Group, Stack, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryState } from '../../components/QueryState';
import { formatDateTime } from '../../lib/format';
import { t } from '../../i18n/ru';
import type { CoverageRow, CoverageView } from './api';
import { FilterBar } from './FilterBar';
import { Block, CsvButton, PersonLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

const stateColor: Record<string, string> = { Never: 'gray', Active: 'teal', Gone: 'orange' };

type Toggle = 'never' | 'gone' | 'outdated';

const matches: Record<Toggle, (row: CoverageRow) => boolean> = {
  never: (r) => r.state === 'Never',
  gone: (r) => r.state === 'Gone',
  outdated: (r) => r.outdated,
};

const toggleLabel: Record<Toggle, string> = { never: t.usage.onlyNever, gone: t.usage.onlyGone, outdated: t.usage.onlyOutdated };

/** «Охват»: все учётки ToadFlow — присылает ли человек данные ToadTools, какой версии, когда последний раз. Период не действует. */
export function CoveragePage() {
  const { filter } = useUsageFilter();
  const path = withQuery('/api/usage/coverage', '', { discipline: filter.discipline });
  const coverage = useApi<CoverageView>(['usage', 'coverage', path], path, { keepPrevious: true });
  const [on, setOn] = useState<Toggle[]>([]);

  const columns: Column<CoverageRow>[] = [
    { key: 'name', title: t.usage.person, render: (r) => <PersonLink id={r.user.id} name={r.user.name} />, sort: (r) => r.user.name },
    { key: 'role', title: t.usage.role, render: (r) => t.role[r.user.role as keyof typeof t.role] ?? r.user.role, sort: (r) => r.user.role },
    { key: 'disciplines', title: t.usage.disciplines, render: (r) => r.user.disciplines.join(', ') || '—' },
    { key: 'state', title: t.usage.state, render: (r) => <Badge variant="light" color={stateColor[r.state]}>{t.usage.coverageStates[r.state]}</Badge>, sort: (r) => r.state },
    {
      key: 'toadTools', title: t.usage.toadTools, sort: (r) => r.toadTools,
      render: (r) => (r.toadTools ? <Group gap={6}>{r.toadTools}{r.outdated && <Badge size="xs" color="orange" variant="light">{t.usage.outdated}</Badge>}</Group> : '—'),
    },
    { key: 'last', title: t.usage.lastSession, render: (r) => formatDateTime(r.lastSessionAt), sort: (r) => r.lastSessionAt },
    { key: 'machines', title: t.usage.machines, render: (r) => r.machines.join(', ') || '—' },
  ];

  return (
    <Stack gap="md">
      <FilterBar periodless />
      <QueryState query={coverage}>
        {(view) => {
          const rows = on.length === 0 ? view.rows : view.rows.filter((r) => on.some((toggle) => matches[toggle](r)));
          return (
            <Block actions={(
              <CsvButton fileName="охват" header={[t.usage.person, t.usage.role, t.usage.disciplines, t.usage.state, t.usage.toadTools, t.usage.lastSession, t.usage.machines]}
                rows={() => rows.map((r) => [r.user.name, t.role[r.user.role as keyof typeof t.role] ?? r.user.role, r.user.disciplines.join(', '),
                  t.usage.coverageStates[r.state], r.toadTools, formatDateTime(r.lastSessionAt), r.machines.join(', ')])} />
            )}>
              <Text size="sm" c="dimmed">{t.usage.minVersion(view.minToadTools, view.latestToadTools)}</Text>
              <Group gap="xl">
                {(Object.keys(matches) as Toggle[]).map((toggle) => (
                  <Stack key={toggle} gap={2}>
                    <Checkbox label={toggleLabel[toggle]} checked={on.includes(toggle)}
                      onChange={(e) => setOn(e.currentTarget.checked ? [...on, toggle] : on.filter((x) => x !== toggle))} />
                    <Text size="xs" c="dimmed">{`${toggleLabel[toggle]}: ${view.rows.filter(matches[toggle]).length} из ${view.rows.length}`}</Text>
                  </Stack>
                ))}
              </Group>
              <DataTable rows={rows} columns={columns} rowKey={(r) => r.user.id} search={(r) => r.user.name} />
            </Block>
          );
        }}
      </QueryState>
    </Stack>
  );
}
