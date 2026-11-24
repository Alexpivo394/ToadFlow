import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';

export type UsagePeriod = '7d' | '30d' | '90d' | 'custom';

/** Фильтр экранов «Аналитики»; живёт в адресе — ссылкой можно поделиться. */
export interface UsageFilter {
  period: UsagePeriod;
  /** Свой период: дни yyyy-MM-dd включительно. */
  from?: string;
  to?: string;
  discipline?: string;
  user?: string;
  machine?: string;
  revitYear?: string;
}

const periods: UsagePeriod[] = ['7d', '30d', '90d', 'custom'];
const keys = ['from', 'to', 'discipline', 'user', 'machine', 'revitYear'] as const;
const days: Record<Exclude<UsagePeriod, 'custom'>, number> = { '7d': 7, '30d': 30, '90d': 90 };

export function readFilter(params: URLSearchParams): UsageFilter {
  const period = params.get('period') as UsagePeriod | null;
  const filter: UsageFilter = { period: period && periods.includes(period) ? period : '30d' };
  for (const key of keys) {
    const value = params.get(key);
    if (value) filter[key] = value;
  }
  if (filter.period !== 'custom') {
    delete filter.from;
    delete filter.to;
  }
  return filter;
}

/** Пишет фильтр в копию параметров адреса; чужие параметры (вкладка, сортировка) остаются. */
export function writeFilter(params: URLSearchParams, filter: UsageFilter): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const key of ['period', ...keys]) next.delete(key);
  if (filter.period !== '30d') next.set('period', filter.period);
  for (const key of keys) {
    const value = filter[key];
    if (value && (filter.period === 'custom' || (key !== 'from' && key !== 'to'))) next.set(key, value);
  }
  next.delete('page');
  return next;
}

/** Местный день yyyy-MM-dd. */
export function isoDay(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/**
 * Строка параметров для API. Готовый период — только начало (конец — сегодня по поясу сервера); день, а не момент: строка
 * входит в ключ запроса и не должна меняться на каждом рендере.
 */
export function filterQuery(filter: UsageFilter, today: Date = new Date()): string {
  const params = new URLSearchParams();
  if (filter.period === 'custom') {
    if (filter.from) params.set('from', filter.from);
    if (filter.to) params.set('to', filter.to);
  } else if (filter.period !== '30d') {
    const from = new Date(today);
    from.setDate(from.getDate() - (days[filter.period] - 1));
    params.set('from', isoDay(from));
  }
  for (const key of ['discipline', 'user', 'machine', 'revitYear'] as const) if (filter[key]) params.set(key, filter[key]!);
  return params.toString();
}

export function useUsageFilter() {
  const [params, setParams] = useSearchParams();
  const filter = useMemo(() => readFilter(params), [params]);
  const setFilter = useCallback(
    (patch: Partial<UsageFilter>) => setParams((current) => writeFilter(current, { ...readFilter(current), ...patch }), { replace: true }),
    [setParams],
  );
  const query = useMemo(() => filterQuery(filter), [filter]);
  return { filter, setFilter, query };
}

/** Адрес API с параметрами фильтра и своими. */
export function withQuery(path: string, query: string, extra: Record<string, string | undefined> = {}): string {
  const params = new URLSearchParams(query);
  for (const [key, value] of Object.entries(extra)) if (value) params.set(key, value);
  const text = params.toString();
  return text ? `${path}?${text}` : path;
}
