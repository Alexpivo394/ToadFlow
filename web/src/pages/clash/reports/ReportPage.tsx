import { useCallback, useEffect, useRef, useState } from 'react';
import { Link as RouterLink, useParams, useSearchParams } from 'react-router';
import {
  Alert, Anchor, Box, Breadcrumbs, Button, Grid, Group, Pagination, Popover, SegmentedControl, Select, Stack, Text, Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { ReportLevelView, ReportPage as Page, ReportView } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { ListFilters } from '../../../components/collisions/ListFilters';
import { ClosedStatuses, OpenStatuses, type ListParams } from '../../../components/collisions/useListParams';
import { Panel } from '../../../components/ui';
import { copyText } from '../../../lib/clipboard';
import { formatDuration } from '../../../lib/format';
import { ReportCollisionDrawer } from './ReportCollisionDrawer';
import { ReportExport } from './ReportExport';
import { NoLevel, ReportRows } from './ReportRows';
import { ReportSummary } from './ReportSummary';
import { type ReportSelection, ReportTree, ReportTreeSelect, readMine, writeMine } from './ReportTree';

const PageSize = 100;
const n = (value: number) => value.toLocaleString('ru-RU');

/** Дата и время целиком: «31.10.2026 12:05». */
export function formatFullDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Выбор и фильтры отчёта — в строке адреса: ссылку можно переслать. */
const filterKeys = ['discipline', 'pair', 'status', 'assignee', 'level', 'model', 'q', 'sort'] as const;

const reportSorts = [{ value: 'level', label: 'По уровням' }, { value: 'depth', label: 'По глубине' }, { value: 'number', label: 'По номеру' }];

/** Пункты фильтра «Уровень»: уровни выборки в их порядке, одинаковые уровни разных пар — одним пунктом с общим числом. */
function levelOptions(levels: ReportLevelView[] | undefined) {
  const counts = new Map<string, number>();
  for (const l of levels ?? []) counts.set(l.level, (counts.get(l.level) ?? 0) + l.count);
  return [...counts].map(([level, count]) => ({ value: level || '-', label: `${level || NoLevel} · ${n(count)}` }));
}

/**
 * Отчёт утверждённого прогона: шапка, слева дерево разделов и пар, справа сводка или список коллизий выбранного раздела/пары
 * по уровням. Проектировщик без выбора попадает в свой раздел. Выгрузки Excel и HTML; координатор копирует сообщение для чата.
 * Статусы в строках — текущие; у старого прогона — плашка со ссылкой на более новый.
 */
export function ReportPage() {
  const { runId } = useParams();
  const { can, canWork, user, disciplines: myDisciplines } = useAuth();
  const [params, setParams] = useSearchParams();
  const [copying, setCopying] = useState(false);
  const [copied, setCopied] = useState(false);
  const [mine, setMine] = useState(readMine);
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 2000);
    return () => clearTimeout(timer);
  }, [copied]);
  const report = useApi<ReportView>(['clash-collisions', 'report', runId], `/api/clash/reports/${runId}`);
  const scope = params.get('scope') === 'open' ? 'open' : 'changes';
  const page = Math.max(1, Number(params.get('page') ?? 1) || 1);
  const collisionId = params.get('collision');
  const sort = params.get('sort') ?? 'level';
  const selection: ReportSelection = params.get('pair')
    ? { kind: 'pair', pair: params.get('pair')! }
    : params.get('discipline') ? { kind: 'discipline', name: params.get('discipline')! } : { kind: 'summary' };

  // Общая панель фильтров поверх прежних параметров адреса: без статуса — пресет области («изменения» — все, «открытые» —
  // открытые), без сортировки — по уровням. Поиск применяется по Enter или уходу из поля.
  const scopeStatus = scope === 'open' ? 'open' : 'all';
  const rawStatus = params.get('status');
  const listParams: ListParams = {
    q: params.get('q') ?? undefined,
    status: !rawStatus ? scopeStatus : rawStatus === OpenStatuses ? 'open' : rawStatus === ClosedStatuses ? 'closed' : rawStatus,
    level: params.get('level') ?? undefined,
    sort: sort as ListParams['sort'],
    group: 'none',
    page,
  };

  const update = useCallback((patch: Record<string, string | null>, resetPage = true) =>
    setParams((p) => {
      const next = new URLSearchParams(p);
      for (const [k, v] of Object.entries(patch)) if (v == null || v === '') next.delete(k); else next.set(k, v);
      if (resetPage) next.delete('page');
      return next;
    }, { replace: true }), [setParams]);

  const onFilters = (patch: Partial<ListParams>) => {
    const changes: Record<string, string | null> = {};
    if ('q' in patch) changes.q = patch.q ?? null;
    if ('status' in patch) changes.status = !patch.status || patch.status === scopeStatus ? null : patch.status;
    if ('level' in patch) changes.level = patch.level ?? null;
    if ('sort' in patch) changes.sort = !patch.sort || patch.sort === 'level' ? null : patch.sort;
    update(changes);
  };

  const select = useCallback((s: ReportSelection) => update({
    discipline: s.kind === 'discipline' ? s.name : null,
    pair: s.kind === 'pair' ? s.pair : null,
    level: null,
    collision: null,
  }), [update]);

  // Проектировщик без выбора в адресе попадает в свой раздел — один раз на отчёт, дальше «Сводка» остаётся сводкой.
  const view = report.data;
  const landed = useRef<string | null>(null);
  useEffect(() => {
    if (!view || !user || landed.current === runId) return;
    landed.current = runId ?? null;
    if (user.role !== 'Designer' || params.get('discipline') || params.get('pair')) return;
    const own = view.disciplines.find((d) => myDisciplines.includes(d.name));
    if (own) select({ kind: 'discipline', name: own.name });
  }, [view, user, runId, params, myDisciplines, select]);

  const filters = new URLSearchParams({ scope });
  for (const key of filterKeys) {
    const value = params.get(key);
    if (value) filters.set(key, value);
  }
  const listing = selection.kind !== 'summary';
  const listQuery = new URLSearchParams(filters);
  listQuery.set('page', String(page));
  listQuery.set('pageSize', String(PageSize));
  const rows = useApi<Page>(['clash-collisions', 'report-rows', runId, listQuery.toString()],
    listing ? `/api/clash/reports/${runId}/collisions?${listQuery}` : null);
  // Заголовки уровней — по выборке со всеми фильтрами; пункты фильтра «Уровень» — без него, чтобы список не сужался.
  const levelsQuery = new URLSearchParams(filters);
  levelsQuery.delete('sort');
  const levels = useApi<ReportLevelView[]>(['clash-collisions', 'report-levels', runId, levelsQuery.toString()],
    listing ? `/api/clash/reports/${runId}/levels?${levelsQuery}` : null);
  const optionsQuery = new URLSearchParams(levelsQuery);
  optionsQuery.delete('level');
  const options = useApi<ReportLevelView[]>(['clash-collisions', 'report-levels', runId, optionsQuery.toString()],
    listing ? `/api/clash/reports/${runId}/levels?${optionsQuery}` : null);

  // Листание в панели: соседняя строка страницы, на краю — соседняя страница. Адрес получает `collision=first|last`,
  // после загрузки той страницы маркер заменяется id её первой или последней строки.
  const items = rows.data?.items;
  const pageCount = Math.max(1, Math.ceil((rows.data?.total ?? 0) / PageSize));
  const marker = collisionId === 'first' || collisionId === 'last';
  const index = items && collisionId ? items.findIndex((r) => r.collision.id === collisionId) : -1;
  useEffect(() => {
    if (!marker || !items) return;
    const target = collisionId === 'first' ? items[0] : items[items.length - 1];
    update({ collision: target?.collision.id ?? null }, false);
  }, [marker, collisionId, items, update]);
  const prev = index > 0
    ? () => update({ collision: items![index - 1].collision.id }, false)
    : index === 0 && page > 1 ? () => update({ page: String(page - 1), collision: 'last' }, false) : undefined;
  const next = index >= 0 && index < items!.length - 1
    ? () => update({ collision: items![index + 1].collision.id }, false)
    : index >= 0 && page < pageCount ? () => update({ page: String(page + 1), collision: 'first' }, false) : undefined;
  const step = useRef({ prev, next });
  step.current = { prev, next };
  const drawerOpen = collisionId !== null;
  useEffect(() => {
    if (!drawerOpen) return;
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
      const go = e.key === 'ArrowDown' ? step.current.next : e.key === 'ArrowUp' ? step.current.prev : undefined;
      if (!go) return;
      e.preventDefault();
      go();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [drawerOpen]);

  if (report.error) return <Alert color="red" title="Отчёт недоступен">{report.error.message}</Alert>;
  if (!view) return null;

  const run = view.run;
  const at = formatFullDateTime(run.finishedAt);
  const total = rows.data?.total ?? 0;

  const copyChat = async () => {
    setCopying(true);
    try {
      const chat = await apiFetch<{ text: string }>(`/api/clash/reports/${runId}/chat?baseUrl=${encodeURIComponent(window.location.origin)}`);
      if (await copyText(chat.text)) setCopied(true);
      else notifications.show({ color: 'red', message: 'Браузер не дал скопировать текст' });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    } finally {
      setCopying(false);
    }
  };

  const onMine = (value: boolean) => {
    setMine(value);
    writeMine(value);
  };

  const more = [params.get('model'), params.get('assignee')].filter(Boolean).length;

  return (
    <Stack gap="sm">
      <Breadcrumbs>
        <Anchor component={RouterLink} to="/clash/checks" size="sm">Проверки</Anchor>
        <Text size="sm" c="dimmed">{view.check.object ?? 'Без проекта'}</Text>
        <Anchor component={RouterLink} to={`/clash/checks/${run.partId}`} size="sm">{view.check.name}</Anchor>
        <Text size="sm">Отчёт от {at}</Text>
      </Breadcrumbs>

      {view.newerRunId && (
        <Alert color="yellow" title={`Есть более новый прогон от ${formatFullDateTime(view.newerRunAt)}`}>
          Этот отчёт показывает изменения своего прогона; статусы в строках — текущие.{' '}
          <Anchor component={RouterLink} to={`/clash/reports/${view.newerRunId}?${filters}`}>Открыть новый отчёт</Anchor>
        </Alert>
      )}

      <Group justify="space-between" align="flex-start" wrap="wrap">
        <Stack gap={2}>
          <Title order={2}>Отчёт от {at}</Title>
          <Text size="sm" c="dimmed">
            v{run.version} · {run.trigger === 'schedule' ? 'по расписанию' : 'вручную'}{run.startedBy ? ` (${run.startedBy})` : ''} · поиск {formatDuration(run.searchSeconds)}
          </Text>
        </Stack>
        <Group gap="xs" align="flex-start">
          <ReportExport runId={run.runId} view={view} selection={selection} filters={filters} total={rows.data?.total}
            fileBase={`Коллизии ${[view.check.object, view.check.name].filter(Boolean).join(' ')}`} />
          {can('Operator') && <Button size="xs" loading={copying} onClick={copyChat}>{copied ? 'Скопировано' : 'Скопировать сообщение для чата'}</Button>}
        </Group>
      </Group>

      <Group gap="xs">
        <SegmentedControl size="xs" value={scope} onChange={(v) => update({ scope: v === 'open' ? 'open' : null })}
          data={[{ value: 'changes', label: 'Изменения прогона' }, { value: 'open', label: 'Открытые сейчас' }]} />
      </Group>

      <Grid gap="sm">
        <Grid.Col span={{ base: 12, md: 3 }} visibleFrom="md">
          <Panel>
            <ReportTree view={view} scope={scope} selection={selection} myDisciplines={myDisciplines} mine={mine} onMine={onMine} onSelect={select} />
          </Panel>
        </Grid.Col>
        <Grid.Col span={{ base: 12, md: 9 }}>
          <Stack gap="sm">
            <Box hiddenFrom="md">
              <ReportTreeSelect view={view} scope={scope} selection={selection} onSelect={select} />
            </Box>
            {selection.kind === 'summary' ? (
              <ReportSummary view={view} onPair={(pair) => select({ kind: 'pair', pair })} />
            ) : (
              <Panel>
                <Stack gap="xs">
                  <Group gap="xs" wrap="wrap">
                    <ListFilters params={listParams} fields={['q', 'status', 'level', 'sort']} qCommit sorts={reportSorts}
                      levelData={levelOptions(options.data)} onChange={onFilters} />
                    <Popover position="bottom-end" withinPortal>
                      <Popover.Target>
                        <Button size="xs" variant={more ? 'light' : 'default'}>Ещё{more ? ` (${more})` : ''}</Button>
                      </Popover.Target>
                      <Popover.Dropdown>
                        <Stack gap="xs">
                          <Select size="xs" w={220} placeholder="Модель" clearable searchable comboboxProps={{ withinPortal: false }}
                            data={view.models.map((m) => m.name)} value={params.get('model')} onChange={(v) => update({ model: v })} />
                          <Select size="xs" w={220} placeholder="Кто правит" clearable comboboxProps={{ withinPortal: false }}
                            value={params.get('assignee')} onChange={(v) => update({ assignee: v })}
                            data={[{ value: 'Both', label: 'обе стороны' }, { value: 'SideA', label: 'сторона A' }, { value: 'SideB', label: 'сторона B' }]} />
                        </Stack>
                      </Popover.Dropdown>
                    </Popover>
                  </Group>
                  <Text size="xs" c="dimmed">{n(total)} коллизий</Text>
                  <ReportRows rows={rows.data?.items ?? []} pairs={view.pairs} levels={levels.data} offset={(page - 1) * PageSize}
                    showPairs={selection.kind === 'discipline'} showLevels={sort === 'level'}
                    selected={collisionId} onOpen={(id) => update({ collision: id }, false)} />
                  {rows.data && rows.data.items.length === 0 && <Text size="sm" c="dimmed">Коллизий с такими условиями нет</Text>}
                  {total > PageSize && (
                    <Pagination size="sm" total={Math.ceil(total / PageSize)} value={page} onChange={(p) => update({ page: String(p) }, false)} />
                  )}
                </Stack>
              </Panel>
            )}
          </Stack>
        </Grid.Col>
      </Grid>

      <ReportCollisionDrawer partId={run.partId} opened={drawerOpen} collisionId={marker ? null : collisionId} canEdit={canWork}
        canMerge={can('Operator')} onClose={() => update({ collision: null }, false)} onPrev={prev} onNext={next} />
    </Stack>
  );
}
