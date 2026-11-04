import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link as RouterLink, useSearchParams } from 'react-router';
import { Alert, Badge, Box, Button, Group, Select, Stack, Text, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { downloadFile } from '../../../api/client';
import { useApi, useApiMutation } from '../../../api/queries';
import { QueryError } from '../../../components/QueryState';
import type { ApprovedRunView, ApprovedVersionView, ClashPartView, CollisionFacets, CollisionRow, CollisionSummary, ReportRunView } from '../../../api/types';
import { CollisionList, type ListItem, type ListSource } from '../../../components/collisions/CollisionList';
import { ListFilters } from '../../../components/collisions/ListFilters';
import { toQuery, useListParams, type ListParams } from '../../../components/collisions/useListParams';
import { Empty } from '../../../components/ui';
import { categoryLabel } from '../../../lib/categories';
import { formatDateTime, formatDuration } from '../../../lib/format';
import { ChecksList } from '../draft/DraftTab';
import { queueText, stateLabel } from '../DraftRunPanel';
import { HitScene } from '../viewer/HitScene';
import { RunsDrawer } from '../reports/RunsDrawer';
import { CollisionCard } from './CollisionCard';


const n = (value: number) => value.toLocaleString('ru-RU');
const PageSize = 50;

/** Строка прогона утверждённой версии: версия, состояние, итог сверки, «Прогнать утверждённую». */
function ApprovedRunLine({ partId, canRun }: { partId: string; canRun: boolean }) {
  const run = useApi<ApprovedRunView | null>(['clash-collisions', partId, 'run'], `/api/clash/parts/${partId}/approved/run`);
  const start = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/approved/run`, { invalidate: ['clash-collisions', 'clash-parts'] });
  const cancel = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/approved/run/cancel`, { invalidate: ['clash-collisions', 'clash-parts'] });
  const current = run.data ?? null;
  const active = current?.run.state === 'Queued' || current?.run.state === 'Running';
  const s = current?.summary;
  const stats = current?.run.stats;
  const runs = useApi<ReportRunView[]>(['clash-collisions', partId, 'runs'], `/api/clash/parts/${partId}/approved/runs`);
  const latest = runs.data?.[0] ?? null;
  const [runsOpened, setRunsOpened] = useState(false);

  return (
    <Stack gap={4}>
      <Group gap="sm">
        {canRun && <Button loading={start.isPending} onClick={() => start.mutate()}>Прогнать утверждённую</Button>}
        {canRun && active && <Button variant="default" onClick={() => cancel.mutate()} loading={cancel.isPending}>Отменить</Button>}
        {current && <Text size="sm" fw={500}>v{current.version}</Text>}
        {current && <Badge color={stateLabel[current.run.state].color} variant="light">{stateLabel[current.run.state].label}</Badge>}
        {current?.run.stage && <Text size="sm">{current.run.stage}</Text>}
        {queueText(current?.run) && <Text size="sm" c="dimmed">{queueText(current?.run)}</Text>}
        {current && <Text size="xs" c="dimmed">{formatDateTime(current.run.finishedAt ?? current.run.createdAt)}</Text>}
        {stats && <Text size="xs" c="dimmed">· поиск {formatDuration(stats.broadSeconds + stats.narrowSeconds)}</Text>}
        {!current && <Text size="sm" c="dimmed">утверждённая версия ещё не прогонялась</Text>}
        {latest && <Button component={RouterLink} to={`/clash/reports/${latest.runId}`} size="compact-sm" variant="light">Отчёт</Button>}
        {latest && <Button size="compact-sm" variant="subtle" onClick={() => setRunsOpened(true)}>Все прогоны</Button>}
      </Group>
      {s && (
        // Итог прогона — чипы с числами, как плитки отчёта; служебное «без пересчёта N пар» — в подсказке «Найдено».
        <Group gap={6} wrap="wrap" data-testid="run-summary">
          <Tooltip disabled={s.reusedPairs === 0} label={`Без пересчёта ${n(s.reusedPairs)} пар: элементы не менялись с прошлого прогона`}>
            <Text size="sm" fw={500}>Найдено {n(s.found)}</Text>
          </Tooltip>
          {([
            ['новых', s.new, 'red'], ['вернулись', s.returned, 'grape'], ['активных', s.active, 'orange'], ['исправлено', s.fixed, 'green'],
            ['ушли из-за правил', s.goneByRules, 'gray'], ['не проверено', s.unchecked, 'yellow'], ['фиктивных', s.fake, 'gray'],
          ] as const).map(([label, value, color]) => (
            <Badge key={label} size="sm" variant="light" color={value > 0 ? color : 'gray'} tt="none">{label} {n(value)}</Badge>
          ))}
        </Group>
      )}
      <QueryError query={run} title="Прогон не загрузился" />
      {current?.run.state === 'Failed' && current.run.error && <Alert color="red" title="Прогон не удался">{current.run.error}</Alert>}
      <RunsDrawer partId={partId} opened={runsOpened} onClose={() => setRunsOpened(false)} />
    </Stack>
  );
}

/**
 * Выгрузка списка проверки в Excel или HTML с его фильтрами (статусы, проверка, поиск): «открытые сейчас» отчёта последнего
 * удачного прогона — та же выборка, что в списке.
 */
function ExportButtons({ partId, filters }: { partId: string; filters: URLSearchParams }) {
  const runs = useApi<ReportRunView[]>(['clash-collisions', partId, 'runs'], `/api/clash/parts/${partId}/approved/runs`);
  const latest = runs.data?.[0] ?? null;
  const [exporting, setExporting] = useState<string | null>(null);
  if (!latest) return null;

  const exportFile = async (format: 'xlsx' | 'html') => {
    const query = new URLSearchParams(filters);
    query.delete('page');
    query.delete('pageSize');
    query.set('scope', 'open');
    query.set('baseUrl', window.location.origin);
    setExporting(format);
    try {
      await downloadFile(`/api/clash/reports/${latest.runId}/export.${format}?${query}`,
        `Коллизии ${new Date().toISOString().slice(0, 10)}.${format}`);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось выгрузить', message: (error as Error).message });
    } finally {
      setExporting(null);
    }
  };

  return (
    <Group gap={4}>
      <Text size="xs" c="dimmed">Выгрузить список:</Text>
      <Button size="compact-xs" variant="default" loading={exporting === 'xlsx'} onClick={() => exportFile('xlsx')}>Excel</Button>
      {/* HTML сервер выгружает только по паре — без выбранной пары кнопка объясняет, что сделать. */}
      <Tooltip label="Выберите пару разделов — HTML выгружается по паре" disabled={!!filters.get('pair')}>
        <Button size="compact-xs" variant="default" loading={exporting === 'html'} data-disabled={!filters.get('pair') || undefined}
          onClick={() => { if (filters.get('pair')) void exportFile('html'); }}>HTML</Button>
      </Tooltip>
    </Group>
  );
}

export function Side({ row, side }: { row: CollisionRow; side: 'A' | 'B' }) {
  const a = side === 'A';
  const family = (a ? row.familyA : row.familyB) ?? (a ? row.typeA : row.typeB) ?? '—';
  return (
    <Text size="xs" c={a ? 'red.7' : 'blue.7'} truncate>
      {categoryLabel(a ? row.categoryA : row.categoryB)} · {family} · ID {a ? row.elementIdA : row.elementIdB}
    </Text>
  );
}

/**
 * Утверждённая проверка: строка прогона и три колонки — проверки, общий список коллизий (фильтры в адресе, по умолчанию
 * открытые; группы по элементу; массовые действия), 3D и карточка выбранной коллизии. ↑/↓ листают коллизии страницы.
 * canEdit — статусы, «кто правит», примечания (проектировщик и старше); canRun — прогоны и объединение (координатор).
 */
export function ApprovedTab({ partId, canEdit, canRun }: { partId: string; canEdit: boolean; canRun: boolean }) {
  const [search, setSearch] = useSearchParams();
  const [list, setList] = useListParams({});
  const [more, setMore] = useState(false);
  const [items, setItems] = useState<ListItem[]>([]);
  const versions = useApi<ApprovedVersionView[]>(['clash-parts', partId, 'versions'], `/api/clash/parts/${partId}/versions`);
  const summary = useApi<CollisionSummary>(['clash-collisions', partId, 'summary'], `/api/clash/parts/${partId}/approved/summary`);
  const part = useApi<ClashPartView>(['clash-parts', partId], `/api/clash/parts/${partId}`);
  const checks = useMemo(() => summary.data?.checks ?? [], [summary.data]);
  const check = search.get('check');
  const element = search.get('element');
  const collisionId = search.get('collision');
  const disciplines = useMemo(() => [...new Set((part.data?.models ?? []).map((m) => m.discipline).filter((d): d is string => !!d))].sort(), [part.data]);

  const update = useCallback((patch: Record<string, string | null>) =>
    setSearch((p) => {
      const next = new URLSearchParams(p);
      for (const [k, v] of Object.entries(patch)) if (v == null) next.delete(k); else next.set(k, v);
      return next;
    }, { replace: true }), [setSearch]);

  const extra = useCallback((query: string) => {
    const q = new URLSearchParams(query);
    if (check) q.set('check', check);
    if (element) q.set('element', element);
    return q.toString();
  }, [check, element]);
  // Группировка по элементу требует раздел: без выбранного — первый раздел части; пока часть не загружена, разделов нет —
  // список без группировки (запрос группы без раздела сервер отклоняет).
  const shown = useMemo<ListParams>(() => {
    if (list.group !== 'element' || list.side) return list;
    return disciplines[0] ? { ...list, side: disciplines[0] } : { ...list, group: 'none' };
  }, [list, disciplines]);
  const source = useMemo<ListSource>(() => ({
    list: (p) => `/api/clash/parts/${partId}/approved/collisions?${extra(toQuery(p))}&pageSize=${PageSize}`,
    partId,
  }), [partId, extra]);
  const facets = useApi<CollisionFacets>(['clash-list', partId, 'facets', extra(toQuery(shown))],
    `/api/clash/parts/${partId}/approved/facets?${extra(toQuery(shown))}`);

  const index = items.findIndex((i) => i.row.id === collisionId);
  const current = index >= 0 ? items[index].row : null;

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

  const open = (target: number | string) => {
    if (typeof target === 'string') update({ collision: target });
    else {
      const found = items.find((i) => i.row.number === target);
      if (found) update({ collision: found.row.id });
      else setList({ q: String(target) });
    }
  };

  // «Ещё в N коллизиях → показать»: группы по элементу с фильтром элемента — одним изменением адреса.
  const showElement = (key: string, discipline: string) =>
    setSearch((p) => {
      const next = new URLSearchParams(p);
      next.set('group', 'element');
      next.set('side', discipline);
      next.set('element', key);
      next.delete('page');
      return next;
    }, { replace: true });

  if (versions.data && versions.data.length === 0)
    return <Empty>Часть ещё не утверждена — утвердите черновик на вкладке «Черновик», и здесь появятся коллизии со статусами.</Empty>;

  return (
    <Stack gap="sm">
      <ApprovedRunLine partId={partId} canRun={canRun} />
      <QueryError query={summary} title="Коллизии проверки не загрузились" />
      {(list.discipline || list.stuckDays) && (
        <Group gap="xs">
          {list.discipline && <Badge variant="light">Раздел: {list.discipline}</Badge>}
          {list.discipline && <Button size="compact-xs" variant="subtle" onClick={() => setList({ discipline: undefined })}>все разделы</Button>}
          {list.stuckDays && <Badge color="orange" variant="light">Только висящие: без движения дольше {list.stuckDays} дн.</Badge>}
          {list.stuckDays && <Button size="compact-xs" variant="subtle" onClick={() => setList({ stuckDays: undefined })}>показать все</Button>}
        </Group>
      )}
      <Group align="stretch" gap={0} wrap="nowrap" h="calc(100vh - 290px)" mih={560}
        style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
        <Box w={{ base: 200, xl: 250 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
          <Group justify="space-between" px="sm" pt="sm">
            <Text size="xs" fw={700} c="dimmed" tt="uppercase">Проверки</Text>
            {check && <Button size="compact-xs" variant="subtle" onClick={() => update({ check: null, page: null })}>все</Button>}
          </Group>
          <ChecksList checks={checks} selected={check} onSelect={(key) => update({ check: key, collision: null, page: null })} />
        </Box>

        <Stack gap={0} w={{ base: 400, xl: 470 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
          <Stack gap={6} p="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
            <ListFilters params={list} facets={facets.data ?? undefined} fields={['q', 'status', 'pair', 'level', 'sort', 'group', 'more']}
              onChange={setList} onOpen={open} onMore={() => setMore((m) => !m)} />
            {more && (
              <Group gap="xs">
                <Select size="xs" w={180} aria-label="Модель" placeholder="Модель" clearable searchable value={list.model ?? null}
                  data={(facets.data?.models ?? []).map((m) => ({ value: m.value, label: `${m.value} — ${n(m.count)}` }))}
                  onChange={(v) => setList({ model: v ?? undefined })} />
                <Select size="xs" w={180} aria-label="Категория" placeholder="Категория" clearable searchable value={list.category ?? null}
                  data={(facets.data?.categories ?? []).map((c) => ({ value: c.value, label: `${categoryLabel(c.value)} — ${n(c.count)}` }))}
                  onChange={(v) => setList({ category: v ?? undefined })} />
              </Group>
            )}
            {shown.group === 'element' && (
              <Group gap={6}>
                <Text size="xs" c="dimmed">Элементы раздела</Text>
                <Select size="xs" w={150} aria-label="Раздел для группировки" allowDeselect={false} data={disciplines} value={shown.side ?? null}
                  onChange={(v) => setList({ side: v ?? undefined })} />
              </Group>
            )}
            {element && (
              <Group gap="xs">
                <Badge variant="light">Один элемент</Badge>
                <Button size="compact-xs" variant="subtle" onClick={() => update({ element: null, page: null })}>все элементы</Button>
              </Group>
            )}
            <ExportButtons partId={partId} filters={new URLSearchParams(extra(toQuery(shown)))} />
          </Stack>
          <CollisionList source={source} params={shown} selectedId={collisionId} onSelect={(id) => update({ collision: id })} bulk={canEdit} canEdit={canEdit}
            onParams={setList} onItems={setItems} />
        </Stack>

        <Stack gap={0} p="xs" style={{ flex: 1, minWidth: 480 }}>
          {collisionId ? (
            <>
              <Box style={{ flex: 3, minHeight: 0 }}>
                <HitScene runId="" number={current?.number ?? 0} collisionId={collisionId}
                  title={current ? `№ ${current.number} · глубина ${Math.round(current.depthMm)} мм` : undefined} />
              </Box>
              <Box pt="xs" data-testid="card-column" style={{ flex: 2, minHeight: 0, overflowY: 'auto' }}>
                <CollisionCard key={collisionId} collisionId={collisionId} canEdit={canEdit} canMerge={canRun} partId={partId} partName={part.data?.name}
                  onShowElement={showElement} />
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
