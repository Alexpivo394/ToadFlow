import { useSearchParams } from 'react-router';
import { Button, Group, Select, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedCallback } from '@mantine/hooks';
import { useInfiniteQuery } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import type { SystemEvent } from '../../api/types';
import { DataTable } from '../../components/DataTable';
import { Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';

const pageSize = 50;

const periods: Record<string, number | null> = { '1d': 1, '7d': 7, '30d': 30, all: null };

/**
 * Журнал действий — события системы: когда, кто, что. Фильтры — кто (по вхождению), вид, период; живут в адресе и уходят на
 * сервер. Листание — «Показать ещё».
 */
export function EventsPage() {
  const [params, setParams] = useSearchParams();
  const actor = params.get('actor') ?? '';
  const kind = params.get('kind');
  const period = params.get('period') ?? 'all';
  const kinds = useApi<string[]>(['events', 'kinds'], '/api/events/kinds');
  const set = (key: string, value: string | null) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (value) next.set(key, value);
      else next.delete(key);
      return next;
    }, { replace: true });
  const setActor = useDebouncedCallback((value: string) => set('actor', value.trim() || null), 300);

  const query = new URLSearchParams();
  if (actor) query.set('actor', actor);
  if (kind) query.set('kind', kind);
  const days = periods[period] ?? null;
  // Начало периода — с точностью до минуты: ключ запроса не меняется на каждой отрисовке.
  if (days) query.set('from', new Date(Math.floor(Date.now() / 60_000) * 60_000 - days * 86_400_000).toISOString());
  query.set('take', String(pageSize));

  const events = useInfiniteQuery({
    queryKey: ['events', query.toString()],
    queryFn: ({ pageParam }) => apiFetch<SystemEvent[]>(`/api/events?${query}${pageParam ? `&before=${pageParam}` : ''}`),
    initialPageParam: 0,
    getNextPageParam: (last) => (last.length === pageSize ? last[last.length - 1].id : undefined),
  });
  const rows = events.data?.pages.flat() ?? [];

  return (
    <Panel>
      <Stack gap="sm">
        <Group gap="sm">
          <TextInput w={220} aria-label="Кто" placeholder="кто (имя)…" defaultValue={actor} onChange={(e) => setActor(e.currentTarget.value)} />
          <Select w={240} aria-label="Вид" placeholder="Любой вид" clearable searchable data={kinds.data ?? []} value={kind}
            onChange={(value) => set('kind', value)} />
          <Select w={160} aria-label="Период" allowDeselect={false} value={period} onChange={(value) => set('period', value === 'all' ? null : value)}
            data={[
              { value: '1d', label: 'За сутки' },
              { value: '7d', label: 'За 7 дней' },
              { value: '30d', label: 'За 30 дней' },
              { value: 'all', label: 'За всё время' },
            ]} />
        </Group>
        <DataTable rows={rows} rowKey={(e) => String(e.id)} server={{ total: rows.length, pageSize: Math.max(rows.length, 1) }} empty="Событий нет"
          columns={[
            { key: 'at', title: 'Когда', width: 150, render: (e) => <Text size="sm" c="dimmed">{formatDateTime(e.at)}</Text> },
            { key: 'actor', title: 'Кто', width: 220, render: (e) => <Text size="sm">{e.actorName}</Text> },
            {
              key: 'text', title: 'Что',
              render: (e) => <Text size="sm" c={e.kind === 'AgentWentOffline' ? 'red' : e.kind === 'InstanceKilled' ? 'orange' : undefined}>{e.text}</Text>,
            },
          ]} />
        {events.hasNextPage && (
          <Button variant="default" w="fit-content" onClick={() => events.fetchNextPage()} loading={events.isFetchingNextPage}>
            Показать ещё
          </Button>
        )}
      </Stack>
    </Panel>
  );
}
