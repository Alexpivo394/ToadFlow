import { Button, Group, Select, TextInput } from '@mantine/core';
import { useEffect, useState, type ClipboardEvent } from 'react';
import type { CollisionFacets, ValueCount } from '../../api/types';
import type { ListParams } from './useListParams';

export type ListField = 'q' | 'status' | 'pair' | 'level' | 'assignee' | 'sort' | 'group' | 'more';

const n = (value: number) => value.toLocaleString('ru-RU');
const All = '__all';

const statusOptions = [
  { value: 'open', label: 'Открытые' },
  { value: 'closed', label: 'Закрытые' },
  { value: 'all', label: 'Все' },
  { value: 'New', label: 'Новые' },
  { value: 'Returned', label: 'Вернувшиеся' },
  { value: 'FixedPending', label: 'Отмечены исправленными' },
  { value: 'Fake', label: 'Фиктивные' },
  { value: 'Fixed', label: 'Исправленные' },
  { value: 'GoneByRules', label: 'Ушли из-за правил' },
];

const assigneeOptions = [
  { value: 'all', label: 'Все' },
  { value: 'mine', label: 'Мне править' },
  { value: 'taken', label: 'Взяла другая сторона' },
];

const sortOptions = [
  { value: 'depth', label: 'По глубине' },
  { value: 'number', label: 'По номеру' },
  { value: 'level', label: 'По уровню' },
];

const groupOptions = [
  { value: 'none', label: 'Без группировки' },
  { value: 'element', label: 'По элементу' },
];

/** Номер коллизии или её Id из вставленного текста «ToadClash №123 — часть» и/или ссылки с `collision=`; ссылка важнее. */
export function parseClashText(text: string): number | string | null {
  const link = /[?&]collision=([0-9a-zA-Z-]+)/.exec(text);
  if (link) return link[1];
  const number = /ToadClash\s*№\s*(\d+)/i.exec(text);
  return number ? Number(number[1]) : null;
}

function options(values: ValueCount[] | undefined, all: string, empty?: string) {
  const list = values ?? [];
  const total = list.reduce((sum, v) => sum + v.count, 0);
  return [
    { value: All, label: `${all} (${n(total)})` },
    ...list.map((v) => ({ value: v.value, label: `${v.value === '-' && empty ? empty : v.value} — ${n(v.count)}` })),
  ];
}

type Option = { value: string; label: string };

/**
 * Поиск, который применяется по Enter или уходу из поля — не на каждую букву. Смена адреса переписывает набранный текст;
 * если список поиск не принял (черновик по номеру открывает попадание), поле возвращается к действующему значению.
 */
function CommitSearch({ value, onCommit, onPaste }: { value: string; onCommit: (q: string | undefined) => void; onPaste: (e: ClipboardEvent<HTMLInputElement>) => void }) {
  const [text, setText] = useState(value);
  const [commits, setCommits] = useState(0);
  useEffect(() => setText(value), [value, commits]);
  const commit = (raw: string) => {
    const q = raw.trim();
    if (q === value) return;
    onCommit(q || undefined);
    setCommits((c) => c + 1);
  };
  return (
    <TextInput size="xs" w={180} placeholder="№, ID или UniqueId" aria-label="Поиск" value={text} onPaste={onPaste}
      onChange={(e) => setText(e.currentTarget.value)} onBlur={(e) => commit(e.currentTarget.value)}
      onKeyDown={(e) => { if (e.key === 'Enter') commit(e.currentTarget.value); }} />
  );
}

/**
 * Панель фильтров списка коллизий. `fields` — какие элементы показать. Варианты пары и уровня — из фасетов, с числами.
 * Вставка в поиск текста «ToadClash №123» или ссылки на коллизию открывает её. Поиск применяется по Enter или уходу из
 * поля; `sorts`, `groups` и `levelData` заменяют стандартные варианты, когда у списка они свои (отчёт, черновик).
 */
export function ListFilters({ params, facets, fields, onChange, onOpen, onMore, sorts, groups, levelData }: {
  params: ListParams;
  facets?: CollisionFacets;
  fields: ListField[];
  onChange: (patch: Partial<ListParams>) => void;
  onOpen?: (target: number | string) => void;
  onMore?: () => void;
  sorts?: Option[];
  groups?: Option[];
  levelData?: Option[];
}) {
  const has = (field: ListField) => fields.includes(field);
  const onPaste = (e: ClipboardEvent<HTMLInputElement>) => {
    const target = parseClashText(e.clipboardData.getData('text'));
    if (target === null || !onOpen) return;
    e.preventDefault();
    onOpen(target);
  };
  const statuses = statusOptions.some((o) => o.value === params.status) ? statusOptions : [...statusOptions, { value: params.status, label: params.status }];

  return (
    <Group gap="xs" wrap="wrap">
      {has('q') && <CommitSearch value={params.q ?? ''} onCommit={(q) => onChange({ q })} onPaste={onPaste} />}
      {has('status') && (
        <Select size="xs" w={190} aria-label="Статус" allowDeselect={false} value={params.status} data={statuses}
          onChange={(v) => onChange({ status: v ?? 'open' })} />
      )}
      {has('pair') && (
        <Select size="xs" w={200} aria-label="Пара разделов" allowDeselect={false} value={params.pair ?? All} data={options(facets?.pairs, 'Все пары')}
          onChange={(v) => onChange({ pair: !v || v === All ? undefined : v })} />
      )}
      {has('level') && (levelData ? (
        <Select size="xs" w={200} aria-label="Уровень" placeholder="Уровень" clearable searchable value={params.level ?? null} data={levelData}
          onChange={(v) => onChange({ level: v ?? undefined })} />
      ) : (
        <Select size="xs" w={180} aria-label="Уровень" allowDeselect={false} searchable value={params.level ?? All}
          data={options(facets?.levels, 'Все уровни', 'Без уровня')} onChange={(v) => onChange({ level: !v || v === All ? undefined : v })} />
      ))}
      {has('assignee') && (
        <Select size="xs" w={190} aria-label="Кто правит" allowDeselect={false} value={params.assignee ?? 'all'} data={assigneeOptions}
          onChange={(v) => onChange({ assignee: (v ?? 'all') as ListParams['assignee'] })} />
      )}
      {has('sort') && (
        <Select size="xs" w={160} aria-label="Сортировка" allowDeselect={false} value={params.sort} data={sorts ?? sortOptions}
          onChange={(v) => onChange({ sort: (v ?? params.sort) as ListParams['sort'] })} />
      )}
      {has('group') && (
        <Select size="xs" w={170} aria-label="Группировка" allowDeselect={false} value={params.group} data={groups ?? groupOptions}
          onChange={(v) => onChange({ group: (v ?? params.group) as ListParams['group'] })} />
      )}
      {has('more') && onMore && <Button size="compact-xs" variant="subtle" onClick={onMore}>Ещё фильтры</Button>}
    </Group>
  );
}
