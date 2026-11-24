import { Alert, Badge, Group, Select, Stack, Table, Text } from '@mantine/core';
import { useNavigate, useSearchParams } from 'react-router';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryState } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import type { CommandRow, CommandsView, CommandSource } from './api';
import { EmptyState } from './EmptyState';
import { FilterBar } from './FilterBar';
import { formatDuration } from './format';
import { Block, CsvButton } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

const sources: CommandSource[] = ['ToadTools', 'Revit', 'Addin'];
const sourceColor: Record<CommandSource, string> = { ToadTools: 'teal', Revit: 'blue', Addin: 'grape' };

/** Доля ошибок — только у ToadTools: запуск с ленты исхода не знает. */
const failedShare = (r: CommandRow) => (r.source === 'ToadTools' && r.count > 0 ? Math.round((r.failed / r.count) * 100) : null);

/** «Инструменты»: рейтинг команд — запуски, люди, доля ошибок; команды ToadTools, которые давно не запускали. */
export function ToolsPage() {
  const [params, setParams] = useSearchParams();
  const source = (sources as string[]).includes(params.get('source') ?? '') ? (params.get('source') as CommandSource) : undefined;
  const { query } = useUsageFilter();
  const path = withQuery('/api/usage/commands', query, { source });
  const commands = useApi<CommandsView>(['usage', 'commands', path], path, { keepPrevious: true });
  const navigate = useNavigate();

  const setSource = (value: string | null) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (value) next.set('source', value);
      else next.delete('source');
      next.delete('page');
      return next;
    }, { replace: true });

  const columns: Column<CommandRow>[] = [
    { key: 'name', title: t.usage.title, render: (r) => <Text size="sm" title={r.commandId}>{r.name}</Text>, sort: (r) => r.name },
    { key: 'source', title: t.usage.source, render: (r) => <Badge variant="light" color={sourceColor[r.source]}>{t.usage.sources[r.source]}</Badge>, sort: (r) => r.source },
    { key: 'tab', title: t.usage.tab, render: (r) => r.tab ?? '—', sort: (r) => r.tab },
    { key: 'count', title: t.usage.runs, render: (r) => r.count, sort: (r) => r.count },
    { key: 'people', title: t.usage.people, render: (r) => r.people, sort: (r) => r.people },
    { key: 'failed', title: t.usage.failedShare, render: (r) => (failedShare(r) == null ? '—' : `${failedShare(r)} %`), sort: failedShare },
    { key: 'avg', title: t.usage.avg, render: (r) => formatDuration(r.avgMs), sort: (r) => r.avgMs },
  ];

  return (
    <Stack gap="md">
      <FilterBar basis={commands.data?.basis} />
      <QueryState query={commands}>
        {(view) => (
          <Stack gap="md">
            {view.ribbonOnly && source !== 'ToadTools' && <Alert variant="light" color="gray">{t.usage.ribbonOnly}</Alert>}
            {view.rows.length === 0 ? <EmptyState /> : (
              <Block actions={(
                <Group gap="sm">
                  <Select size="xs" w={200} aria-label={t.usage.source} placeholder={t.usage.allSources} clearable value={source ?? null}
                    data={sources.map((s) => ({ value: s, label: t.usage.sources[s] }))} onChange={setSource} />
                  <CsvButton fileName="инструменты" header={[t.usage.title, t.usage.source, t.usage.tab, t.usage.runs, t.usage.people, `${t.usage.failedShare}, %`, `${t.usage.avg}, с`]}
                    rows={() => view.rows.map((r) => [r.name, t.usage.sources[r.source], r.tab, r.count, r.people, failedShare(r), r.avgMs == null ? null : Math.round(r.avgMs / 1000)])} />
                </Group>
              )}>
                <DataTable rows={view.rows} columns={columns} rowKey={(r) => `${r.source}/${r.commandId}`} search={(r) => `${r.name} ${r.commandId}`}
                  onRowClick={(r) => navigate(`/usage/tools/${r.source}/${encodeURIComponent(r.commandId)}`)} />
              </Block>
            )}
            <Block title={t.usage.unused}>
              {view.unused.length === 0 ? <Text size="sm" c="dimmed">{t.usage.unusedEmpty}</Text> : (
                <Table fz="sm">
                  <Table.Thead><Table.Tr><Table.Th>{t.usage.title}</Table.Th><Table.Th>{t.usage.lastRun}</Table.Th></Table.Tr></Table.Thead>
                  <Table.Tbody>
                    {view.unused.map((u) => (
                      <Table.Tr key={u.commandId}>
                        <Table.Td>{u.name}</Table.Td>
                        <Table.Td>{u.lastRunAt ? u.lastRunAt.split('-').reverse().join('.') : t.usage.never}</Table.Td>
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
