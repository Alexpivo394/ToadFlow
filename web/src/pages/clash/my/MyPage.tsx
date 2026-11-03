import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { Box, Card, Group, Select, SimpleGrid, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { CheckView, CollisionFacets, MySummary } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { CollisionList, type ListItem, type ListSource } from '../../../components/collisions/CollisionList';
import { ListFilters } from '../../../components/collisions/ListFilters';
import { toQuery, useListParams, type ListParams } from '../../../components/collisions/useListParams';
import { Empty, PageTitle } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime } from '../../../lib/format';
import { CollisionCard } from '../approved/CollisionCard';
import { HitScene } from '../viewer/HitScene';

const n = (value: number) => value.toLocaleString('ru-RU');
const PageSize = 50;
const NoProject = 'Без проекта';

/** Подсказка счётчика «Новые для меня»: заказчик не понял отметку — имя оставлено, смысл объясняется. */
const NewForMeHint = 'Коллизии, которые вы ещё не открывали: появились или вернулись после вашего последнего просмотра. В списке они отмечены синей точкой; откройте карточку — отметка снимется';

function Counter({ label, value, active, onClick, caption, hint }: {
  label: string;
  value: number | undefined;
  active: boolean;
  onClick: () => void;
  caption?: string;
  hint?: string;
}) {
  const button = (
    <UnstyledButton onClick={onClick} aria-label={`${label}: ${value ?? 0}`}>
      <Card withBorder padding="sm" radius="md" bg={active ? 'var(--mantine-primary-color-light)' : undefined}>
        <Text size="xs" c="dimmed">{label}</Text>
        <Group gap={6} align="baseline">
          <Text fw={700} size="xl">{value == null ? '—' : n(value)}</Text>
          {caption && <Text size="xs" c="dimmed">{caption}</Text>}
        </Group>
      </Card>
    </UnstyledButton>
  );
  return hint ? <Tooltip label={hint} multiline w={300} withArrow openDelay={300}>{button}</Tooltip> : button;
}

/**
 * «Мои коллизии»: коллизии разделов профиля по всем утверждённым проверкам — та же выборка, что у плагина. Счётчики
 * переключают «Мне править», «Взяла другая сторона» и «Новые для меня» (ещё не открытые); фильтры — общая панель списка,
 * выбор запоминается. Справа — 3D и карточка, ↑/↓ листают.
 */
/** Как часто проектировщику перечитывать «Мои»: живые обновления ему закрыты, сервер их не шлёт. */
export const DesignerPollMs = 60_000;

export function MyPage() {
  const { can, canWork, isStaff } = useAuth();
  const queryClient = useQueryClient();

  // Проектировщик не получает живых обновлений: раз в минуту — свежие счётчики, проверки и список.
  useEffect(() => {
    if (isStaff) return;
    const timer = setInterval(() => {
      for (const key of ['clash-my', 'clash-checks', 'clash-list']) void queryClient.invalidateQueries({ queryKey: [key] });
    }, DesignerPollMs);
    return () => clearInterval(timer);
  }, [isStaff, queryClient]);
  const [search, setSearch] = useSearchParams();
  const [list, setList] = useListParams({ assignee: 'mine' }, 'my-list');
  const [items, setItems] = useState<ListItem[]>([]);
  const fresh = search.get('new') === '1';
  const project = search.get('object');
  const partId = search.get('partId');
  const collisionId = search.get('collision');
  const summary = useApi<MySummary>(['clash-my', 'summary'], '/api/clash/my/summary');
  const checks = useApi<CheckView[]>(['clash-checks'], '/api/clash/checks');

  const update = useCallback((patch: Record<string, string | null>) =>
    setSearch((p) => {
      const next = new URLSearchParams(p);
      for (const [k, v] of Object.entries(patch)) if (v == null) next.delete(k); else next.set(k, v);
      return next;
    }, { replace: true }), [setSearch]);

  const extra = useCallback((query: string) => {
    const q = new URLSearchParams(query);
    if (fresh) {
      q.set('since', 'unseen');
      q.set('scope', 'all');
    }
    if (project) q.set('object', project === NoProject ? '' : project);
    if (partId) q.set('partId', partId);
    return q.toString();
  }, [fresh, project, partId]);
  const source = useMemo<ListSource>(() => ({
    list: (p) => `/api/clash/my?${extra(toQuery(p))}&pageSize=${PageSize}`,
    partId: partId ?? undefined,
    my: true,
  }), [extra, partId]);
  const facets = useApi<CollisionFacets>(['clash-my', 'facets', extra(toQuery(list))], `/api/clash/my/facets?${extra(toQuery(list))}`);

  const index = items.findIndex((i) => i.row.id === collisionId);
  const current = index >= 0 ? items[index] : null;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
      const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0;
      if (!step || items.length === 0) return;
      e.preventDefault();
      update({ collision: items[Math.min(items.length - 1, Math.max(0, index + step))].row.id });
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [items, index, update]);

  const switchTo = (assignee: ListParams['assignee'] | 'new') => {
    if (assignee === 'new') {
      update({ new: '1', page: null });
      return;
    }
    update({ new: null });
    setList({ assignee });
  };

  const open = (target: number | string) => {
    if (typeof target === 'string') update({ collision: target });
    else {
      const found = items.find((i) => i.row.number === target);
      if (found) update({ collision: found.row.id });
      else setList({ q: String(target) });
    }
  };

  if (summary.data && summary.data.disciplines.length === 0)
    return (
      <>
        <PageTitle>{t.clash.myTitle}</PageTitle>
        <Empty>Разделы не указаны — попросите координатора указать их в профиле</Empty>
      </>
    );

  const projects = [...new Set((checks.data ?? []).map((c) => c.object ?? NoProject))];
  const parts = (checks.data ?? []).filter((c) => !project || (c.object ?? NoProject) === project);

  const lastRunAt = (checks.data ?? []).map((c) => c.lastRunAt).filter((d): d is string => d !== null).sort().at(-1);

  return (
    <Stack gap="sm">
      <PageTitle>{t.clash.myTitle}</PageTitle>
      {!isStaff && lastRunAt && <Text size="sm" c="dimmed">Последний прогон: {formatDateTime(lastRunAt)}</Text>}
      <SimpleGrid cols={3} spacing="sm" maw={720}>
        <Counter label="Мне править" value={summary.data?.toFix} active={!fresh && list.assignee === 'mine'} onClick={() => switchTo('mine')} />
        <Counter label="Взяла другая сторона" value={summary.data?.takenByOther} active={!fresh && list.assignee === 'taken'} onClick={() => switchTo('taken')} />
        <Counter label="Новые для меня" value={summary.data?.newForMe} active={fresh} onClick={() => switchTo('new')}
          caption="ещё не открывали" hint={NewForMeHint} />
      </SimpleGrid>
      <Group gap="xs" wrap="wrap">
        <Text size="sm" c="dimmed">Разделы: {summary.data?.disciplines.join(', ') ?? ''}</Text>
        <Select size="xs" w={160} placeholder="Проект" clearable data={projects} value={project} aria-label="Проект"
          onChange={(v) => update({ object: v, partId: null, page: null })} />
        <Select size="xs" w={200} placeholder="Проверка" clearable data={parts.map((c) => ({ value: c.partId, label: c.name }))} value={partId}
          aria-label="Проверка" onChange={(v) => update({ partId: v, page: null })} />
        <ListFilters params={list} facets={facets.data ?? undefined} fields={['q', 'status', 'pair', 'level', 'sort', 'group']} onChange={setList} onOpen={open} />
      </Group>

      <Group align="stretch" gap={0} wrap="nowrap" h="calc(100vh - 330px)" mih={560}
        style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
        <Stack gap={0} w={{ base: 400, xl: 460 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
          <CollisionList source={source} params={list} selectedId={collisionId} onSelect={(id) => update({ collision: id })} bulk={canWork} canEdit={canWork}
            onParams={setList} onItems={setItems}
            extra={(item) => item.my && (
              <>
                <Text size="xs" fw={600} truncate>{item.my.partName} · {item.my.object ?? NoProject}</Text>
                {item.my.takenBy && !item.my.mineToFix && (
                  <Text size="xs" c="dimmed">взял {item.my.takenBy}{item.my.takenAt ? `, ${formatDateTime(item.my.takenAt)}` : ''}</Text>
                )}
              </>
            )} />
        </Stack>

        <Stack gap={0} p="xs" style={{ flex: 1, minWidth: 480 }}>
          {collisionId ? (
            <>
              <Box style={{ flex: 3, minHeight: 0 }}>
                <HitScene runId="" number={current?.row.number ?? 0} collisionId={collisionId}
                  title={current?.my ? `№ ${current.row.number} · ${current.my.partName} · глубина ${Math.round(current.row.depthMm)} мм` : undefined} />
              </Box>
              <Box pt="xs" style={{ flex: 2, minHeight: 0 }}>
                <CollisionCard key={collisionId} collisionId={collisionId} canEdit={canWork} canMerge={can('Operator')}
                  partId={current?.my?.partId} partName={current?.my?.partName} />
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
