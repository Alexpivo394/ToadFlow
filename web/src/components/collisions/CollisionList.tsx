import { Badge, Button, Checkbox, CloseButton, Group, ScrollArea, Stack, Text, UnstyledButton } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { type MouseEvent, type ReactNode, useEffect, useMemo, useState } from 'react';
import { ApiError, apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import type {
  BulkFilter, BulkRefusal, BulkRequest, BulkResult, CollisionPage, CollisionRow, ElementGroup, ElementGroupPage, MyCollisionPage, MyCollisionRow,
} from '../../api/types';
import { categoryLabel } from '../../lib/categories';
import { Side } from '../../pages/clash/approved/ApprovedTab';
import { StatusBadge } from '../../pages/clash/approved/StatusBadge';
import { BulkDialog, type BulkChoice } from './BulkDialog';
import { SelectionBar } from './SelectionBar';
import { ClosedStatuses, OpenStatuses, type ListParams } from './useListParams';

const n = (value: number) => value.toLocaleString('ru-RU');

/** Наибольшее число коллизий в одном массовом действии — как на сервере. */
export const BulkLimit = 5000;

/**
 * Откуда список. `list` — адрес страницы списка по параметрам (без группировки — CollisionPage или MyCollisionPage, с
 * `group=element` — ElementGroupPage). `partId` — часть для «все по фильтру»; без неё выделение шлётся по частям строк.
 * `my` и `filterExtra` — фильтр «Моих» для «все по фильтру».
 */
export type ListSource = {
  list: (p: ListParams) => string;
  partId?: string;
  my?: boolean;
  filterExtra?: Partial<BulkFilter>;
};

/** Строка списка: коллизия и, в «Моих», её проверка. */
export type ListItem = { row: CollisionRow; partId?: string; my?: MyCollisionRow };

type AnyPage = CollisionPage | MyCollisionPage;

function items(page: AnyPage | undefined | null): ListItem[] {
  return (page?.items ?? []).map((item) =>
    'collision' in item ? { row: item.collision, partId: item.partId, my: item } : { row: item });
}

/** Тот же адрес с другими параметрами запроса. */
function withQuery(url: string, patch: Record<string, string | null>): string {
  const parsed = new URL(url, 'http://local');
  for (const [key, value] of Object.entries(patch)) {
    if (value === null) parsed.searchParams.delete(key);
    else parsed.searchParams.set(key, value);
  }
  return parsed.pathname + parsed.search;
}

function pageSizeOf(url: string): number {
  const size = Number(new URL(url, 'http://local').searchParams.get('pageSize'));
  return Number.isFinite(size) && size > 0 ? size : 50;
}

/** Фильтр «все по фильтру» — те же поля, что у списка. */
export function bulkFilterOf(p: ListParams, source: ListSource): BulkFilter {
  const filter: BulkFilter = { ...source.filterExtra };
  filter.status = p.status === 'open' ? OpenStatuses : p.status === 'closed' ? ClosedStatuses : p.status;
  if (p.q?.trim()) filter.q = p.q.trim();
  if (p.pair) filter.pair = p.pair;
  if (p.level) filter.level = p.level;
  if (p.model) filter.model = p.model;
  if (p.category) filter.category = p.category;
  if (p.stuckDays) filter.stuckDays = p.stuckDays;
  if (source.my) {
    filter.my = true;
    if (p.assignee === 'mine') filter.scope = 'tofix';
    else if (p.assignee === 'taken') filter.scope = 'taken';
    else if (p.assignee === 'all') filter.scope = 'all';
  }
  return filter;
}

function CollisionLine({ item, active, checked, showBox, bulk, onSelect, onCheck, extra }: {
  item: ListItem;
  active: boolean;
  checked: boolean;
  showBox: boolean;
  bulk: boolean;
  onSelect: () => void;
  onCheck: (e: MouseEvent) => void;
  extra?: ReactNode;
}) {
  const [hover, setHover] = useState(false);
  const row = item.row;
  return (
    <Group gap={4} wrap="nowrap" px="xs" py={4} onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      bg={active ? 'var(--mantine-primary-color-light)' : undefined} style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      {bulk && (
        <Checkbox size="xs" aria-label={`Выделить №${row.number}`} checked={checked} onChange={() => {}} onClick={onCheck}
          style={{ opacity: showBox || hover || checked ? 1 : 0 }} />
      )}
      <UnstyledButton style={{ flex: 1, minWidth: 0 }} onClick={onSelect} aria-label={`Коллизия ${row.number}`}>
        <Group gap={6} wrap="nowrap">
          <Stack gap={2} w={70}>
            <Group gap={4} wrap="nowrap">
              <Text size="xs" c="dimmed">№ {row.number}</Text>
              {item.my?.unseen && <Badge size="xs" color="orange" variant="dot" aria-label="Новая для меня">нов</Badge>}
            </Group>
            <StatusBadge status={row.status} />
          </Stack>
          <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
            {extra}
            <Side row={row} side="A" />
            <Side row={row} side="B" />
          </Stack>
          <Text size="xs">{Math.round(row.depthMm)} мм</Text>
        </Group>
      </UnstyledButton>
    </Group>
  );
}

/** Коллизии раскрытой группы элемента. */
function GroupRows({ url, render }: { url: string; render: (items: ListItem[]) => ReactNode }) {
  const page = useApi<AnyPage>(['clash-list', url], url);
  if (!page.data) return <Text size="xs" c="dimmed" px="lg" py={4}>Загрузка…</Text>;
  return <>{render(items(page.data))}</>;
}

/**
 * Список коллизий: строки страницами или группы по элементу своей стороны (`group=element`). С `bulk` — флажки, полоса
 * выделения и массовые действия. Флажок строки виден при наведении или когда что-то выделено; Shift — диапазон на странице.
 */
export function CollisionList({ source, params, selectedId, onSelect, bulk = false, canEdit = true, onParams, onItems, extra }: {
  source: ListSource;
  params: ListParams;
  selectedId: string | null;
  onSelect: (id: string, item: ListItem) => void;
  bulk?: boolean;
  canEdit?: boolean;
  onParams?: (patch: Partial<ListParams>) => void;
  /** Строки текущей страницы — для ↑/↓ и карточки. */
  onItems?: (items: ListItem[]) => void;
  /** Дополнительная строка над сторонами — например, проверка в «Моих». */
  extra?: (item: ListItem) => ReactNode;
}) {
  const queryClient = useQueryClient();
  const grouped = params.group === 'element';
  const url = source.list(params);
  const page = useApi<AnyPage | ElementGroupPage>(['clash-list', url], url);
  const groups = grouped && page.data && 'groups' in page.data ? page.data : null;
  const flat = !grouped && page.data && !('groups' in page.data) ? page.data : null;
  const rows = useMemo(() => items(flat), [flat]);

  const [selected, setSelected] = useState<Map<string, string | undefined>>(new Map());
  const [byFilter, setByFilter] = useState(false);
  const [groupIds, setGroupIds] = useState<Map<string, ListItem[]>>(new Map());
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [last, setLast] = useState<number | null>(null);
  const [choice, setChoice] = useState<BulkChoice | null>(null);
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState<{ error: string; count: number } | null>(null);
  const [refused, setRefused] = useState<BulkRefusal[]>([]);

  useEffect(() => onItems?.(rows), [rows, onItems]);

  // Новый фильтр — новое выделение: выделенное по старому фильтру легко изменить, не видя его.
  const filterKey = JSON.stringify({ ...params, page: 0 });
  useEffect(() => {
    setSelected(new Map());
    setByFilter(false);
    setExpanded(new Set());
    setGroupIds(new Map());
  }, [filterKey]);

  const total = groups ? groups.collisions : flat?.total ?? 0;
  const count = byFilter ? total : selected.size;
  const filterTotal = source.partId && !grouped ? total : null;

  const toggle = (list: ListItem[], index: number, e: MouseEvent) => {
    const item = list[index];
    setByFilter(false);
    setSelected((prev) => {
      const next = new Map(prev);
      const on = !next.has(item.row.id);
      const range = e.shiftKey && last !== null && list === rows ? list.slice(Math.min(last, index), Math.max(last, index) + 1) : [item];
      for (const r of range) {
        if (on) next.set(r.row.id, r.partId ?? source.partId);
        else next.delete(r.row.id);
      }
      return next;
    });
    if (list === rows) setLast(index);
  };

  const elementKey = (g: ElementGroup) => `${g.model}:${g.uniqueId}`;
  const elementUrl = (g: ElementGroup) => withQuery(source.list({ ...params, group: 'none', side: undefined, page: 1 }), { element: elementKey(g), group: null, side: null });

  const toggleGroup = async (g: ElementGroup) => {
    const key = elementKey(g);
    const known = groupIds.get(key);
    if (known && known.every((i) => selected.has(i.row.id))) {
      setSelected((prev) => {
        const next = new Map(prev);
        for (const i of known) next.delete(i.row.id);
        return next;
      });
      return;
    }
    try {
      const list = known ?? items(await apiFetch<AnyPage>(withQuery(elementUrl(g), { pageSize: String(BulkLimit), page: '1' })));
      setGroupIds((prev) => new Map(prev).set(key, list));
      setByFilter(false);
      setSelected((prev) => {
        const next = new Map(prev);
        for (const i of list) next.set(i.row.id, i.partId ?? source.partId);
        return next;
      });
    } catch (e) {
      notifications.show({ color: 'red', message: e instanceof Error ? e.message : 'Не удалось загрузить коллизии группы' });
    }
  };

  const clear = () => {
    setSelected(new Map());
    setByFilter(false);
  };

  const run = async (note: string | undefined, expected?: number) => {
    if (!choice) return;
    setBusy(true);
    try {
      const base = { action: choice.action, assignee: choice.assignee, note };
      let requests: BulkRequest[];
      if (byFilter) requests = [{ ...base, partId: source.partId!, filter: bulkFilterOf(params, source), expectedCount: expected ?? total }];
      else {
        const byPart = new Map<string, string[]>();
        for (const [id, part] of selected) if (part) byPart.set(part, [...(byPart.get(part) ?? []), id]);
        requests = [...byPart].map(([partId, ids]) => ({ ...base, partId, ids }));
      }
      const results: BulkResult[] = [];
      for (const request of requests) results.push(await apiFetch<BulkResult>('/api/clash/collisions/bulk', { method: 'POST', json: request }));
      const done = results.reduce((sum, r) => sum + r.done, 0);
      setRefused(results.flatMap((r) => r.refused));
      setChoice(null);
      setConflict(null);
      clear();
      notifications.show({ color: 'green', message: `Готово: ${n(done)}` });
      for (const key of ['clash-list', 'clash-collisions', 'clash-my']) void queryClient.invalidateQueries({ queryKey: [key] });
    } catch (e) {
      const body = e instanceof ApiError ? (e.body as { count?: unknown } | null) : null;
      if (e instanceof ApiError && e.status === 409 && typeof body?.count === 'number') setConflict({ error: e.message, count: body.count });
      else notifications.show({ color: 'red', message: e instanceof Error ? e.message : 'Не удалось изменить коллизии' });
    } finally {
      setBusy(false);
    }
  };

  const renderRows = (list: ListItem[]) => list.map((item, index) => (
    <CollisionLine key={item.row.id} item={item} active={item.row.id === selectedId} checked={byFilter || selected.has(item.row.id)}
      showBox={count > 0} bulk={bulk} onSelect={() => onSelect(item.row.id, item)} onCheck={(e) => toggle(list, index, e)} extra={extra?.(item)} />
  ));

  const size = pageSizeOf(url);
  const pages = Math.max(1, Math.ceil((groups ? groups.groups : flat?.total ?? 0) / size));

  return (
    <Stack gap={0} style={{ flex: 1, minHeight: 0 }}>
      {bulk && count > 0 && (
        <SelectionBar count={count} filterTotal={filterTotal} byFilter={byFilter} canEdit={canEdit} onAllByFilter={() => setByFilter(true)}
          onClear={clear} onAction={(c) => { setConflict(null); setChoice(c); }} />
      )}
      {refused.length > 0 && (
        <Stack gap={0} px="xs" py={4} bg="var(--mantine-color-orange-light)">
          <Group justify="space-between">
            <Text size="xs" fw={600}>Не изменены: {n(refused.length)}</Text>
            <CloseButton size="xs" aria-label="Скрыть отказы" onClick={() => setRefused([])} />
          </Group>
          {refused.map((r) => <Text key={r.number} size="xs">№{r.number} — {r.reason}</Text>)}
        </Stack>
      )}
      <ScrollArea style={{ flex: 1 }} type="auto">
        {groups?.items.map((g) => {
          const key = elementKey(g);
          const ids = groupIds.get(key);
          const all = ids !== undefined && ids.length > 0 && ids.every((i) => selected.has(i.row.id));
          const open = expanded.has(key);
          return (
            <Stack key={key} gap={0}>
              <Group gap={4} wrap="nowrap" px="xs" py={4} bg="var(--mantine-color-default-hover)" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
                {bulk && <Checkbox size="xs" aria-label={`Выделить группу ID ${g.elementId}`} checked={byFilter || all} onChange={() => void toggleGroup(g)} />}
                <UnstyledButton style={{ flex: 1, minWidth: 0 }} aria-label={`Группа ID ${g.elementId} — ${g.count}`}
                  onClick={() => setExpanded((prev) => { const next = new Set(prev); if (!next.delete(key)) next.add(key); return next; })}>
                  <Group gap={6} wrap="nowrap">
                    <Text size="xs" c="dimmed">{open ? '▾' : '▸'}</Text>
                    <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                      <Text size="xs" fw={600} truncate>{categoryLabel(g.category)} · {g.family ?? g.type ?? '—'} · ID {g.elementId}</Text>
                      <Text size="xs" c="dimmed" truncate>{g.modelName}{g.level ? ` · ${g.level}` : ''}</Text>
                    </Stack>
                    <Badge size="sm" variant="light">{n(g.count)}</Badge>
                    <Text size="xs" c="dimmed">до {Math.round(g.maxDepthMm)} мм</Text>
                  </Group>
                </UnstyledButton>
              </Group>
              {open && <GroupRows url={elementUrl(g)} render={renderRows} />}
            </Stack>
          );
        })}
        {flat && renderRows(rows)}
        {page.data && (groups ? groups.items.length === 0 : rows.length === 0) && <Text size="sm" c="dimmed" p="sm">Коллизий нет</Text>}
        {page.error && <Text size="sm" c="red" p="sm">{page.error.message}</Text>}
      </ScrollArea>
      <Group justify="space-between" px="xs" py={4} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <Text size="xs" c="dimmed">
          {groups ? `Элементов: ${n(groups.groups)} · коллизий: ${n(groups.collisions)}` : `${n(flat?.total ?? 0)} коллизий · ↑ ↓ листать`}
        </Text>
        {onParams && pages > 1 && (
          <Group gap={4}>
            <Button size="compact-xs" variant="subtle" disabled={params.page <= 1} onClick={() => onParams({ page: params.page - 1 })}>←</Button>
            <Text size="xs">{params.page} / {pages}</Text>
            <Button size="compact-xs" variant="subtle" disabled={params.page >= pages} onClick={() => onParams({ page: params.page + 1 })}>→</Button>
          </Group>
        )}
      </Group>
      <BulkDialog key={choice ? `${choice.action}${choice.assignee ?? ''}` : 'closed'} choice={choice} count={count} busy={busy} conflict={conflict}
        onConfirm={(note, expected) => void run(note, expected)} onCancel={() => { setChoice(null); setConflict(null); }} />
    </Stack>
  );
}
