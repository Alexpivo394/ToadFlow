import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { Alert, Badge, Box, Button, Chip, Group, ScrollArea, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useApi, useApiMutation } from '../../../api/queries';
import type { ApprovedRunView, ApprovedVersionView, CollisionPage, CollisionRow, CollisionStatus, CollisionSummary } from '../../../api/types';
import { Empty } from '../../../components/ui';
import { categoryLabel } from '../../../lib/categories';
import { formatDateTime, formatDuration } from '../../../lib/format';
import { ChecksList } from '../draft/DraftTab';
import { stateLabel } from '../DraftRunPanel';
import { HitScene } from '../viewer/HitScene';
import { CollisionCard } from './CollisionCard';
import { assigneeLabel, openStatuses, statusLabel, StatusBadge } from './StatusBadge';

const n = (value: number) => value.toLocaleString('ru-RU');
const PageSize = 50;
const allStatuses = Object.keys(statusLabel) as CollisionStatus[];

/** Строка прогона утверждённой версии: версия, состояние, итог сверки, «Прогнать утверждённую». */
function ApprovedRunLine({ partId, readOnly }: { partId: string; readOnly: boolean }) {
  const run = useApi<ApprovedRunView | null>(['clash-collisions', partId, 'run'], `/api/clash/parts/${partId}/approved/run`);
  const start = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/approved/run`, { invalidate: ['clash-collisions', 'clash-parts'] });
  const cancel = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/approved/run/cancel`, { invalidate: ['clash-collisions', 'clash-parts'] });
  const current = run.data ?? null;
  const active = current?.run.state === 'Queued' || current?.run.state === 'Running';
  const s = current?.summary;
  const stats = current?.run.stats;

  return (
    <Stack gap={4}>
      <Group gap="sm">
        {!readOnly && <Button loading={start.isPending} onClick={() => start.mutate()}>Прогнать утверждённую</Button>}
        {!readOnly && active && <Button variant="default" onClick={() => cancel.mutate()} loading={cancel.isPending}>Отменить</Button>}
        {current && <Text size="sm" fw={500}>v{current.version}</Text>}
        {current && <Badge color={stateLabel[current.run.state].color} variant="light">{stateLabel[current.run.state].label}</Badge>}
        {current?.run.stage && <Text size="sm">{current.run.stage}</Text>}
        {current && <Text size="xs" c="dimmed">{formatDateTime(current.run.finishedAt ?? current.run.createdAt)}</Text>}
        {stats && <Text size="xs" c="dimmed">· поиск {formatDuration(stats.broadSeconds + stats.narrowSeconds)}</Text>}
        {!current && <Text size="sm" c="dimmed">утверждённая версия ещё не прогонялась</Text>}
      </Group>
      {s && (
        <Text size="sm" c="dimmed">
          Найдено {n(s.found)}: новых {n(s.new)}, активных {n(s.active)}, вернулись {n(s.returned)}; исправлено {n(s.fixed)}, ушли из-за правил {n(s.goneByRules)},
          не проверено {n(s.unchecked)}, фиктивных {n(s.fake)}
          {s.reusedPairs > 0 ? ` · без пересчёта ${n(s.reusedPairs)} пар` : ''}
        </Text>
      )}
      {current?.run.state === 'Failed' && current.run.error && <Alert color="red" title="Прогон не удался">{current.run.error}</Alert>}
    </Stack>
  );
}

function Side({ row, side }: { row: CollisionRow; side: 'A' | 'B' }) {
  const a = side === 'A';
  const family = (a ? row.familyA : row.familyB) ?? (a ? row.typeA : row.typeB) ?? '—';
  return (
    <Text size="xs" c={a ? 'red.7' : 'blue.7'} truncate>
      {categoryLabel(a ? row.categoryA : row.categoryB)} · {family} · ID {a ? row.elementIdA : row.elementIdB}
    </Text>
  );
}

/**
 * Утверждённая проверка: строка прогона и три колонки — проверки, коллизии с фильтром по статусам (по умолчанию открытые),
 * 3D и карточка выбранной коллизии. ↑/↓ листают коллизии страницы.
 */
export function ApprovedTab({ partId, readOnly }: { partId: string; readOnly: boolean }) {
  const [params, setParams] = useSearchParams();
  const [statuses, setStatuses] = useState<string[]>(openStatuses);
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(1);
  const versions = useApi<ApprovedVersionView[]>(['clash-parts', partId, 'versions'], `/api/clash/parts/${partId}/versions`);
  const summary = useApi<CollisionSummary>(['clash-collisions', partId, 'summary'], `/api/clash/parts/${partId}/approved/summary`);
  const checks = useMemo(() => summary.data?.checks ?? [], [summary.data]);
  const check = params.get('check');
  const collisionId = params.get('collision');

  const update = useCallback((patch: Record<string, string | null>) =>
    setParams((p) => {
      const next = new URLSearchParams(p);
      for (const [k, v] of Object.entries(patch)) if (v == null) next.delete(k); else next.set(k, v);
      return next;
    }, { replace: true }), [setParams]);

  const search = new URLSearchParams({ status: statuses.length ? statuses.join(',') : 'all', page: String(page), pageSize: String(PageSize) });
  if (check) search.set('check', check);
  if (query.trim()) search.set('q', query.trim());
  const list = useApi<CollisionPage>(['clash-collisions', partId, 'list', search.toString()], `/api/clash/parts/${partId}/approved/collisions?${search}`);
  const items = useMemo(() => list.data?.items ?? [], [list.data]);
  const index = items.findIndex((r) => r.id === collisionId);
  const current = index >= 0 ? items[index] : null;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
      const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0;
      if (!step || items.length === 0) return;
      e.preventDefault();
      const next = items[Math.min(items.length - 1, Math.max(0, index + step))];
      update({ collision: next.id });
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [items, index, update]);

  if (versions.data && versions.data.length === 0)
    return <Empty>Часть ещё не утверждена — утвердите черновик на вкладке «Черновик», и здесь появятся коллизии со статусами.</Empty>;
  const total = list.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PageSize));

  return (
    <Stack gap="sm">
      <ApprovedRunLine partId={partId} readOnly={readOnly} />
      <Group align="stretch" gap={0} wrap="nowrap" h="calc(100vh - 290px)" mih={560}
        style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
        <Box w={{ base: 200, xl: 250 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
          <Group justify="space-between" px="sm" pt="sm">
            <Text size="xs" fw={700} c="dimmed" tt="uppercase">Проверки</Text>
            {check && <Button size="compact-xs" variant="subtle" onClick={() => { setPage(1); update({ check: null }); }}>все</Button>}
          </Group>
          <ChecksList checks={checks} selected={check} onSelect={(key) => { setPage(1); update({ check: key, collision: null }); }} />
        </Box>

        <Stack gap={0} w={{ base: 370, xl: 430 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
          <Stack gap={6} p="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
            <Chip.Group multiple value={statuses} onChange={(v) => { setPage(1); setStatuses(v); }}>
              <Group gap={4}>
                {allStatuses.map((s) => <Chip key={s} size="xs" value={s}>{statusLabel[s].label}</Chip>)}
              </Group>
            </Chip.Group>
            <TextInput size="xs" placeholder="№, ID или UniqueId" value={query} onChange={(e) => { setPage(1); setQuery(e.currentTarget.value); }} />
          </Stack>
          <ScrollArea style={{ flex: 1 }} type="auto">
            {items.map((r) => (
              <UnstyledButton key={r.id} w="100%" px="xs" py={4} onClick={() => update({ collision: r.id })} aria-label={`Коллизия ${r.number}`}
                bg={r.id === collisionId ? 'var(--mantine-primary-color-light)' : undefined}
                style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
                <Group gap={6} wrap="nowrap">
                  <Stack gap={2} w={70}>
                    <Text size="xs" c="dimmed">№ {r.number}</Text>
                    <StatusBadge status={r.status} />
                  </Stack>
                  <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                    <Side row={r} side="A" />
                    <Side row={r} side="B" />
                    {r.status === 'Fake' && !r.foundNow && <Text size="xs" c="dimmed">сейчас не пересекается</Text>}
                  </Stack>
                  <Stack gap={0} align="flex-end">
                    <Text size="xs">{Math.round(r.depthMm)} мм</Text>
                    <Text size="xs" c="dimmed">{r.assignee === 'Both' ? '' : assigneeLabel[r.assignee]}</Text>
                  </Stack>
                </Group>
              </UnstyledButton>
            ))}
            {list.data && items.length === 0 && <Text size="sm" c="dimmed" p="sm">Коллизий с такими статусами нет</Text>}
          </ScrollArea>
          <Group justify="space-between" px="xs" py={4} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
            <Text size="xs" c="dimmed">{n(total)} коллизий · ↑ ↓ листать</Text>
            {pages > 1 && (
              <Group gap={4}>
                <Button size="compact-xs" variant="subtle" disabled={page <= 1} onClick={() => setPage(page - 1)}>←</Button>
                <Text size="xs">{page} / {pages}</Text>
                <Button size="compact-xs" variant="subtle" disabled={page >= pages} onClick={() => setPage(page + 1)}>→</Button>
              </Group>
            )}
          </Group>
        </Stack>

        <Stack gap={0} p="xs" style={{ flex: 1, minWidth: 480 }}>
          {collisionId ? (
            <>
              <Box style={{ flex: 3, minHeight: 0 }}>
                <HitScene runId="" number={current?.number ?? 0} collisionId={collisionId}
                  title={current ? `№ ${current.number} · глубина ${Math.round(current.depthMm)} мм` : undefined} />
              </Box>
              <Box pt="xs" style={{ flex: 2, minHeight: 0 }}>
                <CollisionCard collisionId={collisionId} readOnly={readOnly} />
              </Box>
            </>
          ) : (
            <Empty>Выберите коллизию в списке</Empty>
          )}
        </Stack>
      </Group>
    </Stack>
  );
}
