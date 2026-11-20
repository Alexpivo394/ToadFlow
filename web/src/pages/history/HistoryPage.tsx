import { createContext, useContext } from 'react';
import { Link, Route, Routes, useLocation, useNavigate, useParams, useSearchParams } from 'react-router';
import { Alert, Anchor, Button, Group, Loader, Pagination, Stack, Table, Tabs, Text } from '@mantine/core';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi, useApiMutation } from '../../api/queries';
import type { JobRow, Page, RunDetails as RunDetailsView, RunRow, SystemEvent } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { ConfirmButton, Empty, PageTitle, Panel, RunStateBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatDateTime, formatDuration } from '../../lib/format';
import { ClashRunsLine } from './ClashRunsLine';
import { toApiQuery } from './filters';
import { Filters, useHistoryFilters } from './HistoryFilters';
import { JobsTable } from './JobsTable';
import type { ModuleName } from '../../spaces/modules';

/** Где открыта история: модуль (нет — все модули, «Ферма») и адрес страницы, от него — ссылки на запуски. */
const Place = createContext<{ module?: ModuleName; base: string }>({ base: '/history' });

/** Запросы истории этой страницы: свой модуль или выбранный в фильтре «Фермы». */
function useScope() {
  const place = useContext(Place);
  const [filters] = useHistoryFilters();
  const module = place.module ?? filters.module;
  return { base: place.base, module, fixed: place.module !== undefined, query: module ? `&module=${module}` : '' };
}

const pageSize = 50;

function Counts({ run }: { run: RunRow }) {
  const c = run.counts;
  const parts = [
    c.succeeded > 0 && <Text key="s" span c="green">{c.succeeded} ✓</Text>,
    c.warning > 0 && <Text key="w" span c="yellow.7">{c.warning} ⚠</Text>,
    c.failed > 0 && <Text key="f" span c="red">{c.failed} ✗</Text>,
    c.running > 0 && <Text key="r" span c="blue">{c.running} в работе</Text>,
    c.queued > 0 && <Text key="q" span c="dimmed">{c.queued} в очереди</Text>,
    c.cancelled > 0 && <Text key="c" span c="dimmed">{c.cancelled} отменено</Text>,
  ].filter(Boolean);
  return parts.length ? <Group gap={8}>{parts}</Group> : <Text c="dimmed">—</Text>;
}

function RunActions({ run }: { run: RunRow }) {
  const { base } = useContext(Place);
  const navigate = useNavigate();
  const retry = useApiMutation<void, { runId: string }>('POST', () => `/api/runs/${run.id}/retry-failed`, { invalidate: ['runs'], success: 'Создан запуск с упавшими моделями' });
  const cancel = useApiMutation<void>('POST', () => `/api/runs/${run.id}/cancel`, { invalidate: ['runs', 'jobs'], success: 'Запуск отменяется' });
  const active = run.state !== 'Completed' && run.state !== 'Cancelled';

  return (
    <RequireRole role="Operator">
      <Group gap={6} wrap="nowrap" onClick={(e) => e.stopPropagation()}>
        {run.counts.failed > 0 && (
          <Button size="compact-xs" variant="default" onClick={() => retry.mutate(undefined, { onSuccess: (r) => navigate(`${base}/runs/${r.runId}`) })}>
            Перезапустить упавшие
          </Button>
        )}
        {active && (
          <ConfirmButton size="compact-xs" variant="default" question={`Отменить запуск «${run.name}»? Модели в очереди не будут выгружены.`} onConfirm={() => cancel.mutate()}>
            Отменить
          </ConfirmButton>
        )}
      </Group>
    </RequireRole>
  );
}

function RunsTab() {
  const [filters, update] = useHistoryFilters();
  const navigate = useNavigate();
  const scope = useScope();
  const runs = useQuery({ queryKey: ['runs', scope.module, filters], queryFn: () => apiFetch<Page<RunRow>>(`/api/runs?${toApiQuery(filters)}${scope.query}`) });

  return (
    <>
      <Filters module={scope.module} fixed={scope.fixed} />
      <Panel>
        {runs.data && runs.data.items.length === 0 ? (
          <Empty>Запусков нет</Empty>
        ) : (
          <Table.ScrollContainer minWidth={900}>
            <Table highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Начало</Table.Th>
                  <Table.Th>Расписание</Table.Th>
                  <Table.Th>Кто запустил</Table.Th>
                  <Table.Th>Состояние</Table.Th>
                  <Table.Th>Итог</Table.Th>
                  <Table.Th>Длительность</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {runs.data?.items.map((run) => (
                  <Table.Tr key={run.id} onClick={() => navigate(`${scope.base}/runs/${run.id}`)} style={{ cursor: 'pointer' }}>
                    <Table.Td>{formatDateTime(run.createdAt)}</Table.Td>
                    <Table.Td>
                      {run.name}
                      {run.warning && <Text size="xs" c="yellow.7" lineClamp={1}>{run.warning}</Text>}
                    </Table.Td>
                    <Table.Td c={run.triggeredBy ? undefined : 'dimmed'}>{run.triggeredBy ? `${run.triggeredBy} (вручную)` : 'по расписанию'}</Table.Td>
                    <Table.Td><RunStateBadge state={run.state} /></Table.Td>
                    <Table.Td><Counts run={run} /></Table.Td>
                    <Table.Td>{formatDuration(run.durationSeconds)}</Table.Td>
                    <Table.Td><RunActions run={run} /></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        {runs.data && runs.data.total > pageSize && (
          <Pagination mt="md" total={Math.ceil(runs.data.total / pageSize)} value={filters.page} onChange={(page) => update({ page })} />
        )}
      </Panel>
    </>
  );
}

function ModelsTab() {
  const [filters, update] = useHistoryFilters();
  const scope = useScope();
  const jobs = useQuery({ queryKey: ['jobs', scope.module, filters], queryFn: () => apiFetch<Page<JobRow>>(`/api/jobs?${toApiQuery(filters)}${scope.query}`) });

  return (
    <>
      <Filters module={scope.module} fixed={scope.fixed} />
      <Panel>
        {jobs.data && jobs.data.items.length === 0 ? <Empty>Моделей нет</Empty> : <JobsTable jobs={jobs.data?.items ?? []} showRun runBase={`${scope.base}/runs`} />}
        {jobs.data && jobs.data.total > pageSize && (
          <Pagination mt="md" total={Math.ceil(jobs.data.total / pageSize)} value={filters.page} onChange={(page) => update({ page })} />
        )}
      </Panel>
    </>
  );
}

function EventsTab() {
  const events = useInfiniteQuery({
    queryKey: ['events'],
    queryFn: ({ pageParam }) => apiFetch<SystemEvent[]>(`/api/events?take=50${pageParam ? `&before=${pageParam}` : ''}`),
    initialPageParam: 0,
    getNextPageParam: (last) => (last.length === 50 ? last[last.length - 1].id : undefined),
  });
  const rows = events.data?.pages.flat() ?? [];

  return (
    <Panel>
      {events.isLoading ? <Loader size="sm" /> : rows.length === 0 ? <Empty>Событий нет</Empty> : (
        <Table>
          <Table.Tbody>
            {rows.map((e) => (
              <Table.Tr key={e.id}>
                <Table.Td c="dimmed" w={130}>{formatDateTime(e.at)}</Table.Td>
                <Table.Td w={200}>{e.actorName}</Table.Td>
                <Table.Td c={e.kind === 'AgentWentOffline' ? 'red' : e.kind === 'InstanceKilled' ? 'orange' : undefined}>{e.text}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {events.hasNextPage && (
        <Button variant="default" mt="md" onClick={() => events.fetchNextPage()} loading={events.isFetchingNextPage}>
          Показать ещё
        </Button>
      )}
    </Panel>
  );
}

function RunDetails() {
  const { id } = useParams();
  const [params] = useSearchParams();
  const { base } = useContext(Place);
  const details = useApi<RunDetailsView>(['runs', id], `/api/runs/${id}`);
  const run = details.data?.run;

  if (details.error) return <Alert color="red">{details.error.message}</Alert>;
  if (!run) return <Loader />;

  return (
    <Stack>
      <Anchor component={Link} to={base} size="sm">
        ← К запускам
      </Anchor>
      <Panel
        title={`Запуск ${formatDateTime(run.createdAt)} — ${run.name}`}
        actions={<RunActions run={run} />}
      >
        <Group gap="lg" mb="sm">
          <RunStateBadge state={run.state} />
          <Text size="sm">{run.triggeredBy ? `Запустил ${run.triggeredBy}` : 'По расписанию'}</Text>
          <Counts run={run} />
          <Text size="sm" c="dimmed">{formatDuration(run.durationSeconds)}</Text>
        </Group>
        {run.warning && <Alert color="yellow" mb="sm">{run.warning}</Alert>}
        {details.data!.jobs.some((j) => j.moduleKind === 'clash-extract') && <ClashRunsLine runId={run.id} />}
        {details.data!.jobs.length === 0 ? <Empty>{run.state === 'Expanding' ? 'Строится список моделей…' : 'Моделей нет'}</Empty> : (
          <JobsTable jobs={details.data!.jobs} initiallyOpen={params.get('job')} runBase={`${base}/runs`} />
        )}
      </Panel>
    </Stack>
  );
}

/**
 * История запусков: внутри модуля (<c>module</c>) — только его записи; без модуля — «Ферма → Очередь и журнал», все модули с
 * фильтром по модулю. <c>base</c> — адрес страницы: от него строятся вкладки и ссылки на запуски.
 */
export function HistoryPage({ module, base = '/history' }: { module?: ModuleName; base?: string }) {
  const location = useLocation();
  const navigate = useNavigate();
  const rest = location.pathname.slice(base.length);
  const tab = rest.startsWith('/models') ? 'models' : rest.startsWith('/events') ? 'events' : 'runs';
  // События системы — общие, а не модуля: вкладка только у общей истории (до страницы «Журнал действий»).
  const withEvents = module === undefined;

  return (
    <Place.Provider value={{ module, base }}>
      <PageTitle>{module ? t.nav.historyPage : base === '/history' ? t.nav.history : t.nav.farmQueue}</PageTitle>
      {!rest.startsWith('/runs/') && (
        <Tabs value={tab} onChange={(v) => navigate(v === 'runs' ? base : `${base}/${v}`)} mb="md">
          <Tabs.List>
            <Tabs.Tab value="runs">Запуски</Tabs.Tab>
            <Tabs.Tab value="models">Все модели</Tabs.Tab>
            {withEvents && <Tabs.Tab value="events">События системы</Tabs.Tab>}
          </Tabs.List>
        </Tabs>
      )}
      <Routes>
        <Route index element={<RunsTab />} />
        <Route path="models" element={<ModelsTab />} />
        {withEvents && <Route path="events" element={<EventsTab />} />}
        <Route path="runs/:id" element={<RunDetails />} />
      </Routes>
    </Place.Provider>
  );
}
