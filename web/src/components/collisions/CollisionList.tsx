import { Badge, Box, Button, Checkbox, CloseButton, Group, ScrollArea, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
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
import { assigneeText, StatusBadge } from '../../pages/clash/approved/StatusBadge';
import { BulkDialog, type BulkChoice } from './BulkDialog';
import { BulkLimit, SelectionBar } from './SelectionBar';
import type { ListParams } from './useListParams';

export { BulkLimit } from './SelectionBar';

const n = (value: number) => value.toLocaleString('ru-RU');

/** Наибольшая страница списка на сервере (DraftResultEndpoints.MaxPageSize). */
const MaxPageSize = 200;
/** Сколько отказов массового действия показывать списком; остальные — «ещё N». */
const ShownRefusals = 20;

/**
 * Откуда список. `list` — адрес страницы списка по параметрам (без группировки — CollisionPage или MyCollisionPage, с
 * `group=element` — ElementGroupPage). `partId` — часть для «все по фильтру»; без неё выделение шлётся по частям строк.
 * `my` — список «Моих»: «все по фильтру» идёт фильтром «Моих».
 */
export type ListSource = {
  list: (p: ListParams) => string;
  partId?: string;
  my?: boolean;
};

/** Строка списка: коллизия и, в «Моих», её проверка. */
export type ListItem = { row: CollisionRow; partId?: string; my?: MyCollisionRow };

type AnyPage = CollisionPage | MyCollisionPage;

/** Выделенная коллизия: её часть (массовое действие идёт по частям), имя части — для итога, разделы сторон — для подписей «Кто правит». */
type Picked = { partId?: string; partName?: string; disciplineA?: string; disciplineB?: string };

/** Разделы сторон, если у всех выделенных они одни и различаются; иначе null — подписи «сторона A / B». */
function commonSides(selected: Map<string, Picked>): { a: string; b: string } | null {
  let sides: { a: string; b: string } | null = null;
  for (const picked of selected.values()) {
    const a = picked.disciplineA?.trim() ?? '';
    const b = picked.disciplineB?.trim() ?? '';
    if (!a || !b || a.toLowerCase() === b.toLowerCase()) return null;
    if (sides && (sides.a !== a || sides.b !== b)) return null;
    sides ??= { a, b };
  }
  return sides;
}

/** Итог массового действия, если в нём есть что показать: отказы, «без изменений», ошибки частей. */
type Outcome = { done: number; unchanged: number; refused: BulkRefusal[]; failures: string[] };

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

/** Что значит синяя точка у строки «Моих» — текст подсказки и имя для чтения с экрана. */
export const UnseenHint = 'Вы ещё не открывали эту коллизию — она появилась или вернулась после вашего последнего просмотра. Откройте карточку, и отметка снимется';

/** Поля запроса списка, которые сервер понимает и в фильтре массового действия. */
const bulkKeys = ['status', 'check', 'discipline', 'pair', 'level', 'model', 'q', 'element', 'category', 'scope', 'object', 'since'] as const;

/**
 * Фильтр «все по фильтру» — ровно запрос списка: берётся из адреса списка, чтобы добавки страницы (проверка, элемент, «новые
 * для меня») попадали в него так же, как в список, и действие не ушло на другой набор коллизий.
 */
export function bulkFilterOf(p: ListParams, source: ListSource): BulkFilter {
  const query = new URL(source.list({ ...p, page: 1 }), 'http://local').searchParams;
  const filter: BulkFilter = {};
  if (source.my) filter.my = true;
  for (const key of bulkKeys) {
    const value = query.get(key);
    if (value !== null) filter[key] = value;
  }
  const stuck = Number(query.get('stuckDays'));
  if (stuck > 0) filter.stuckDays = stuck;
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
              {item.my?.unseen && (
                <Tooltip label={UnseenHint} multiline w={280} withArrow openDelay={200}>
                  <Box role="img" aria-label={UnseenHint} w={8} h={8} style={{ borderRadius: '50%', background: 'var(--mantine-color-blue-6)', flexShrink: 0 }} />
                </Tooltip>
              )}
            </Group>
            <StatusBadge status={row.status} />
          </Stack>
          <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
            {extra}
            <Side row={row} side="A" />
            <Side row={row} side="B" />
            {row.status === 'Fake' && !row.foundNow && <Text size="xs" c="dimmed">сейчас не пересекается</Text>}
          </Stack>
          <Stack gap={0} align="flex-end">
            <Text size="xs">{Math.round(row.depthMm)} мм</Text>
            {row.assignee !== 'Both' && <Text size="xs" c="dimmed">{assigneeText(row.assignee, row)}</Text>}
          </Stack>
        </Group>
      </UnstyledButton>
    </Group>
  );
}

/** Одна страница коллизий раскрытой группы. */
function GroupPage({ url, render }: { url: string; render: (items: ListItem[]) => ReactNode }) {
  const page = useApi<AnyPage>(['clash-list', url], url);
  if (page.error) return <Text size="xs" c="red" px="lg" py={4}>{page.error.message}</Text>;
  if (!page.data) return <Text size="xs" c="dimmed" px="lg" py={4}>Загрузка…</Text>;
  return <>{render(items(page.data))}</>;
}

/** Коллизии раскрытой группы элемента: страницами, «Показать ещё» добавляет следующую. */
function GroupRows({ url, total, render }: { url: string; total: number; render: (items: ListItem[]) => ReactNode }) {
  const size = pageSizeOf(url);
  const [pages, setPages] = useState(1);
  const shown = Math.min(total, pages * size);
  return (
    <>
      {Array.from({ length: pages }, (_, i) => <GroupPage key={i} url={withQuery(url, { page: String(i + 1) })} render={render} />)}
      {total > shown && (
        <Group gap="xs" px="lg" py={4}>
          <Text size="xs" c="dimmed">Показано {n(shown)} из {n(total)}</Text>
          <Button size="compact-xs" variant="subtle" onClick={() => setPages((p) => p + 1)}>Показать ещё</Button>
        </Group>
      )}
    </>
  );
}

function errorText(e: unknown, fallback: string): string {
  return e instanceof Error ? e.message : fallback;
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
  const page = useApi<AnyPage | ElementGroupPage>(['clash-list', url], url, { keepPrevious: true });
  const groups = grouped && page.data && 'groups' in page.data ? page.data : null;
  const flat = !grouped && page.data && !('groups' in page.data) ? page.data : null;
  const rows = useMemo(() => items(flat), [flat]);

  const [selected, setSelected] = useState<Map<string, Picked>>(new Map());
  const [byFilter, setByFilter] = useState(false);
  const [groupIds, setGroupIds] = useState<Map<string, ListItem[]>>(new Map());
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [last, setLast] = useState<number | null>(null);
  const [choice, setChoice] = useState<BulkChoice | null>(null);
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState<{ error: string; count: number } | null>(null);
  const [outcome, setOutcome] = useState<Outcome | null>(null);

  useEffect(() => onItems?.(rows), [rows, onItems]);

  // Новый список — новое выделение: выделенное по старому фильтру легко изменить, не видя его. Ключ — сам запрос списка без
  // страницы: в нём и фильтры панели, и добавки страницы (проверка, элемент, «новые для меня», проект, часть).
  const resetKey = JSON.stringify([source.list({ ...params, page: 1 }), source.partId ?? null, source.my ?? false]);
  useEffect(() => {
    setSelected(new Map());
    setByFilter(false);
    setExpanded(new Set());
    setGroupIds(new Map());
  }, [resetKey]);
  // Диапазон Shift — в пределах одной страницы.
  useEffect(() => setLast(null), [url]);

  const total = groups ? groups.collisions : flat?.total ?? 0;
  const count = byFilter ? total : selected.size;
  const filterTotal = source.partId && !grouped ? total : null;

  const pick = (item: ListItem): Picked => ({
    partId: item.partId ?? source.partId, partName: item.my?.partName, disciplineA: item.row.disciplineA, disciplineB: item.row.disciplineB,
  });

  const toggle = (list: ListItem[], index: number, e: MouseEvent) => {
    const item = list[index];
    setByFilter(false);
    setSelected((prev) => {
      const next = new Map(prev);
      const on = !next.has(item.row.id);
      const range = e.shiftKey && last !== null && list === rows ? list.slice(Math.min(last, index), Math.max(last, index) + 1) : [item];
      for (const r of range) {
        if (on) next.set(r.row.id, pick(r));
        else next.delete(r.row.id);
      }
      return next;
    });
    if (list === rows) setLast(index);
  };

  const elementKey = (g: ElementGroup) => `${g.model}:${g.uniqueId}`;
  const elementUrl = (g: ElementGroup) => withQuery(source.list({ ...params, group: 'none', side: undefined, page: 1 }), { element: elementKey(g), group: null, side: null });

  /** Все коллизии группы — страницами по наибольшему размеру сервера, не больше предела массового действия. */
  const loadGroup = async (g: ElementGroup): Promise<ListItem[]> => {
    const want = Math.min(g.count, BulkLimit);
    const all: ListItem[] = [];
    for (let p = 1; all.length < want; p++) {
      const batch = items(await apiFetch<AnyPage>(withQuery(elementUrl(g), { pageSize: String(MaxPageSize), page: String(p) })));
      all.push(...batch);
      if (batch.length < MaxPageSize) break;
    }
    return all.slice(0, want);
  };

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
      const list = known ?? await loadGroup(g);
      setGroupIds((prev) => new Map(prev).set(key, list));
      setByFilter(false);
      setSelected((prev) => {
        const next = new Map(prev);
        for (const i of list) next.set(i.row.id, pick(i));
        return next;
      });
    } catch (e) {
      notifications.show({ color: 'red', message: errorText(e, 'Не удалось загрузить коллизии группы') });
    }
  };

  const clear = () => {
    setSelected(new Map());
    setByFilter(false);
  };

  const refresh = () => {
    for (const key of ['clash-list', 'clash-collisions', 'clash-my']) void queryClient.invalidateQueries({ queryKey: [key] });
  };

  /** Итог: уведомление «Готово» и, если есть что сказать, полоса итога над списком. */
  const report = (results: BulkResult[], failures: string[]) => {
    const done = results.reduce((sum, r) => sum + r.done, 0);
    const unchanged = results.reduce((sum, r) => sum + (r.unchanged ?? 0), 0);
    const refused = results.flatMap((r) => r.refused);
    setOutcome(refused.length > 0 || unchanged > 0 || failures.length > 0 ? { done, unchanged, refused, failures } : null);
    notifications.show({
      color: failures.length > 0 ? 'orange' : 'green',
      message: [`Готово: ${n(done)}`, ...(unchanged > 0 ? [`без изменений: ${n(unchanged)}`] : []), ...failures].join('; '),
    });
  };

  const runByFilter = async (base: Omit<BulkRequest, 'partId'>, expected?: number) => {
    try {
      const result = await apiFetch<BulkResult>('/api/clash/collisions/bulk', {
        method: 'POST', json: { ...base, partId: source.partId!, filter: bulkFilterOf(params, source), expectedCount: expected ?? total },
      });
      setChoice(null);
      setConflict(null);
      clear();
      report([result], []);
      refresh();
    } catch (e) {
      const body = e instanceof ApiError ? (e.body as { count?: unknown } | null) : null;
      if (e instanceof ApiError && e.status === 409 && typeof body?.count === 'number') setConflict({ error: e.message, count: body.count });
      else notifications.show({ color: 'red', message: errorText(e, 'Не удалось изменить коллизии') });
    }
  };

  /** Выделенные строки — запросом на каждую часть. Ошибка одной части не отменяет сделанное в других. */
  const runByIds = async (base: Omit<BulkRequest, 'partId'>) => {
    const byPart = new Map<string, { ids: string[]; name?: string }>();
    for (const [id, picked] of selected) {
      if (!picked.partId) continue;
      const entry = byPart.get(picked.partId) ?? { ids: [], name: picked.partName };
      entry.ids.push(id);
      byPart.set(picked.partId, entry);
    }
    const results: BulkResult[] = [];
    const failures: string[] = [];
    const failedParts = new Set<string>();
    for (const [partId, { ids, name }] of byPart) {
      try {
        results.push(await apiFetch<BulkResult>('/api/clash/collisions/bulk', { method: 'POST', json: { ...base, partId, ids } }));
      } catch (e) {
        failedParts.add(partId);
        const message = errorText(e, 'Не удалось изменить коллизии');
        failures.push(byPart.size > 1 ? `Часть «${name ?? partId}»: ${message}` : message);
      }
    }
    if (results.length === 0) {
      // Ничего не изменено — диалог остаётся с примечанием, «Продолжить» повторит то же.
      notifications.show({ color: 'red', message: failures.join('; ') });
      return;
    }
    // Изменённые части уходят из выделения: повтор не задвоит примечание. Не изменённые остаются — их можно повторить.
    setChoice(null);
    setConflict(null);
    setByFilter(false);
    setSelected((prev) => new Map([...prev].filter(([, picked]) => picked.partId !== undefined && failedParts.has(picked.partId))));
    report(results, failures);
    refresh();
  };

  const run = async (note: string | undefined, expected?: number) => {
    if (!choice) return;
    setBusy(true);
    try {
      const base = { action: choice.action, assignee: choice.assignee, note };
      if (byFilter) await runByFilter(base, expected);
      else await runByIds(base);
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
  const summary = outcome && [
    `Готово: ${n(outcome.done)}`,
    ...(outcome.unchanged > 0 ? [`без изменений: ${n(outcome.unchanged)}`] : []),
    ...(outcome.refused.length > 0 ? [`не изменены: ${n(outcome.refused.length)}`] : []),
  ].join(' · ');

  return (
    <Stack gap={0} style={{ flex: 1, minHeight: 0 }}>
      {bulk && count > 0 && (
        <SelectionBar count={count} filterTotal={filterTotal} byFilter={byFilter} canEdit={canEdit} canSeen={!!source.my && canEdit}
          sides={byFilter ? null : commonSides(selected)}
          onAllByFilter={() => setByFilter(true)}
          onClear={clear} onAction={(c) => { setConflict(null); setChoice(c); }} />
      )}
      {outcome && (
        <Stack gap={0} px="xs" py={4} bg="var(--mantine-color-orange-light)">
          <Group justify="space-between">
            <Text size="xs" fw={600}>{summary}</Text>
            <CloseButton size="xs" aria-label="Скрыть итог" onClick={() => setOutcome(null)} />
          </Group>
          {outcome.failures.map((f) => <Text key={f} size="xs" c="red">{f}</Text>)}
          {outcome.refused.slice(0, ShownRefusals).map((r) => <Text key={r.number} size="xs">№{r.number} — {r.reason}</Text>)}
          {outcome.refused.length > ShownRefusals && <Text size="xs" c="dimmed">ещё {n(outcome.refused.length - ShownRefusals)}</Text>}
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
              {open && <GroupRows url={elementUrl(g)} total={g.count} render={renderRows} />}
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
