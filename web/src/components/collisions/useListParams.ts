import { useCallback, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';

/**
 * Состояние списка коллизий — одно для проверки, «Моих», отчёта и черновика. Живёт в адресе: ссылкой можно поделиться,
 * «назад» возвращает прежний фильтр.
 */
export type ListParams = {
  q?: string;
  /** Пресет (`open`, `closed`, `all`) или статусы через запятую. */
  status: 'open' | 'closed' | 'all' | string;
  /** Раздел любой из сторон — из «Сводки → Кто отстаёт». */
  discipline?: string;
  pair?: string;
  level?: string;
  assignee?: 'all' | 'mine' | 'taken';
  sort: 'depth' | 'number' | 'level' | 'setpair';
  group: 'none' | 'element' | 'setpair';
  /** Раздел, по элементам которого группировать. */
  side?: string;
  model?: string;
  category?: string;
  stuckDays?: number;
  page: number;
};

export const baseListParams: ListParams = { status: 'open', sort: 'depth', group: 'none', page: 1 };

/** «Открытые» и «Закрытые» — как на сервере (CollisionEndpoints.Open и закрытые статусы). */
export const OpenStatuses = 'New,Active,Returned,FixedPending,Unchecked';
export const ClosedStatuses = 'Fixed,GoneByRules,Fake';

const keys = ['q', 'status', 'discipline', 'pair', 'level', 'assignee', 'sort', 'group', 'side', 'model', 'category', 'stuckDays', 'page'] as const;
type Key = (typeof keys)[number];
const numeric: Key[] = ['stuckDays', 'page'];

function parse(raw: Record<string, unknown>): Partial<ListParams> {
  const result: Record<string, unknown> = {};
  for (const key of keys) {
    const value = raw[key];
    if (value === undefined || value === null || value === '') continue;
    if (numeric.includes(key)) {
      const number = Number(value);
      if (Number.isFinite(number) && number > 0) result[key] = Math.floor(number);
    } else result[key] = String(value);
  }
  return result as Partial<ListParams>;
}

function readStorage(storageKey: string | undefined): Partial<ListParams> {
  if (!storageKey) return {};
  try {
    const text = localStorage.getItem(storageKey);
    const stored = text ? parse(JSON.parse(text) as Record<string, unknown>) : {};
    delete stored.page;
    return stored;
  } catch {
    return {};
  }
}

function writeStorage(storageKey: string | undefined, params: ListParams) {
  if (!storageKey) return;
  try {
    const rest: Partial<ListParams> = { ...params };
    delete rest.page;
    localStorage.setItem(storageKey, JSON.stringify(rest));
  } catch {
    // Хранилище недоступно (приватный режим, запрет сайта) — список работает без памяти выбора.
  }
}

/**
 * Параметры списка из адреса. Значения по умолчанию в адрес не пишутся; смена любого поля, кроме `page`, сбрасывает страницу.
 * С `storageKey` последний выбор запоминается в браузере и подставляется, когда в адресе параметров списка нет.
 */
export function useListParams(defaults: Partial<ListParams>, storageKey?: string): [ListParams, (patch: Partial<ListParams>) => void] {
  const [search, setSearch] = useSearchParams();
  const defaultsKey = JSON.stringify(defaults);
  const all = useMemo<ListParams>(() => ({ ...baseListParams, ...(JSON.parse(defaultsKey) as Partial<ListParams>) }), [defaultsKey]);
  const text = search.toString();
  // Выбор значения по умолчанию поверх сохранённого не меняет адрес (умолчания в адрес не пишутся) — пересчёт по счётчику.
  const [revision, setRevision] = useState(0);

  const params = useMemo<ListParams>(() => {
    void revision;
    const current = new URLSearchParams(text);
    const fromUrl = parse(Object.fromEntries(keys.filter((k) => current.has(k)).map((k) => [k, current.get(k)])));
    return Object.keys(fromUrl).length > 0 ? { ...all, ...fromUrl } : { ...all, ...readStorage(storageKey) };
  }, [text, all, storageKey, revision]);

  const update = useCallback((patch: Partial<ListParams>) => {
    const next: ListParams = { ...params, ...patch };
    if (Object.keys(patch).some((k) => k !== 'page')) next.page = patch.page ?? 1;
    setSearch((prev) => {
      const url = new URLSearchParams(prev);
      for (const key of keys) {
        const value = next[key];
        if (value === undefined || value === '' || value === all[key]) url.delete(key);
        else url.set(key, String(value));
      }
      return url;
    }, { replace: true });
    writeStorage(storageKey, next);
    setRevision((r) => r + 1);
  }, [params, all, setSearch, storageKey]);

  return [params, update];
}

/** Параметры API списка: пресеты статусов раскрываются, «кто правит» в «Моих» — в `scope`. */
export function toQuery(p: ListParams): string {
  const query = new URLSearchParams();
  const status = p.status === 'open' ? OpenStatuses : p.status === 'closed' ? ClosedStatuses : p.status;
  if (status) query.set('status', status);
  const text: [string, string | undefined][] = [['q', p.q?.trim()], ['discipline', p.discipline], ['pair', p.pair], ['level', p.level], ['model', p.model], ['category', p.category]];
  for (const [key, value] of text) if (value) query.set(key, value);
  if (p.stuckDays) query.set('stuckDays', String(p.stuckDays));
  if (p.assignee === 'mine') query.set('scope', 'tofix');
  else if (p.assignee === 'taken') query.set('scope', 'taken');
  else if (p.assignee === 'all') query.set('scope', 'all');
  query.set('sort', p.sort);
  if (p.group !== 'none') {
    query.set('group', p.group);
    if (p.side) query.set('side', p.side);
  }
  query.set('page', String(p.page));
  return query.toString();
}
