import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router';

/** Столбец общей таблицы. */
export interface Column<T> {
  key: string;
  title: string;
  render: (row: T) => ReactNode;
  /** Есть — столбец сортируется щелчком по заголовку; null всегда в конце. */
  sort?: (row: T) => string | number | null;
  /** Фильтр по значениям столбца: строка подходит, если подходит к любому выбранному значению. */
  filter?: { options: { value: string; label: string }[]; match: (row: T, values: string[]) => boolean };
  width?: number;
}

/** Сортировка, поиск, фильтры и страница — живут в адресе, ссылка открывает тот же вид. */
export interface TableState {
  sort: string | null;
  dir: 'asc' | 'desc';
  q: string;
  filters: Record<string, string[]>;
  page: number;
}

export const emptyTableState: TableState = { sort: null, dir: 'asc', q: '', filters: {}, page: 1 };

const collator = new Intl.Collator('ru', { numeric: true, sensitivity: 'base' });

const name = (key: string, urlKey?: string) => (urlKey ? `${urlKey}.${key}` : key);

/** Состояние таблицы из строки запроса; <c>urlKey</c> — приставка параметров, если таблиц на странице несколько. */
export function readTableState(params: URLSearchParams, urlKey?: string): TableState {
  const prefix = name('f.', urlKey);
  const filters: Record<string, string[]> = {};
  for (const [key, value] of params) {
    if (!key.startsWith(prefix)) continue;
    const values = value.split(',').filter(Boolean);
    if (values.length > 0) filters[key.slice(prefix.length)] = values;
  }
  return {
    sort: params.get(name('sort', urlKey)) || null,
    dir: params.get(name('dir', urlKey)) === 'desc' ? 'desc' : 'asc',
    q: params.get(name('q', urlKey)) ?? '',
    filters,
    page: Math.max(1, Number(params.get(name('page', urlKey))) || 1),
  };
}

/** Записывает состояние в копию строки запроса: пустые значения убираются, чужие параметры остаются. */
export function writeTableState(params: URLSearchParams, state: TableState, urlKey?: string): URLSearchParams {
  const next = new URLSearchParams(params);
  const prefix = name('f.', urlKey);
  for (const key of [...next.keys()]) if (key.startsWith(prefix)) next.delete(key);
  const set = (key: string, value: string | null) => (value ? next.set(name(key, urlKey), value) : next.delete(name(key, urlKey)));
  set('sort', state.sort);
  set('dir', state.sort && state.dir === 'desc' ? 'desc' : null);
  set('q', state.q.trim() ? state.q : null);
  for (const [key, values] of Object.entries(state.filters)) if (values.length > 0) next.set(prefix + key, values.join(','));
  set('page', state.page > 1 ? String(state.page) : null);
  return next;
}

/** Состояние таблицы в адресе страницы. Любая правка, кроме смены страницы, возвращает на первую страницу. */
export function useTableState(urlKey?: string): [TableState, (next: Partial<TableState>) => void] {
  const [params, setParams] = useSearchParams();
  const state = readTableState(params, urlKey);
  const update = (next: Partial<TableState>) =>
    setParams((current) => writeTableState(current, { ...readTableState(current, urlKey), page: 1, ...next }, urlKey), { replace: true });
  return [state, update];
}

function compare(a: string | number | null, b: string | number | null): number {
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return collator.compare(String(a), String(b));
}

/** Строки после поиска, фильтров и сортировки (без деления на страницы). */
export function applyTableState<T>(rows: T[], columns: Column<T>[], state: TableState, search?: (row: T) => string): T[] {
  let result = rows;
  const q = state.q.trim().toLocaleLowerCase('ru');
  if (q && search) result = result.filter((row) => search(row).toLocaleLowerCase('ru').includes(q));
  for (const column of columns) {
    const values = state.filters[column.key];
    if (column.filter && values?.length) result = result.filter((row) => column.filter!.match(row, values));
  }
  const sorter = columns.find((c) => c.key === state.sort)?.sort;
  if (sorter) {
    const sign = state.dir === 'desc' ? -1 : 1;
    result = [...result].sort((x, y) => {
      const a = sorter(x);
      const b = sorter(y);
      if (a === null || a === undefined) return b === null || b === undefined ? 0 : 1;
      if (b === null || b === undefined) return -1;
      return sign * compare(a, b);
    });
  }
  return result;
}
