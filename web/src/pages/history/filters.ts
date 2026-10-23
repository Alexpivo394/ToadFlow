import type { JobStatus } from '../../api/types';

export type Period = '1d' | '7d' | '30d' | 'all';

/** Фильтры истории живут в адресе страницы: ссылкой можно поделиться, «назад» работает. */
export interface HistoryFilters {
  period: Period;
  scheduleId?: string;
  status?: JobStatus;
  agentId?: string;
  search?: string;
  page: number;
}

const periodDays: Record<Period, number | null> = { '1d': 1, '7d': 7, '30d': 30, all: null };

export function toSearchParams(filters: HistoryFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.period !== '7d') params.set('period', filters.period);
  if (filters.scheduleId) params.set('schedule', filters.scheduleId);
  if (filters.status) params.set('status', filters.status);
  if (filters.agentId) params.set('agent', filters.agentId);
  if (filters.search?.trim()) params.set('q', filters.search.trim());
  if (filters.page > 1) params.set('page', String(filters.page));
  return params;
}

export function fromSearchParams(params: URLSearchParams): HistoryFilters {
  const period = params.get('period');
  const filters: HistoryFilters = { period: period && period in periodDays ? (period as Period) : '7d', page: Math.max(1, Number(params.get('page')) || 1) };
  const set = <K extends keyof HistoryFilters>(key: K, value: string | null) => {
    if (value) filters[key] = value as HistoryFilters[K];
  };
  set('scheduleId', params.get('schedule'));
  set('status', params.get('status'));
  set('agentId', params.get('agent'));
  set('search', params.get('q'));
  return filters;
}

/** Строка запроса к API истории: период превращается в «с такого-то момента». */
export function toApiQuery(filters: HistoryFilters, now: Date = new Date()): string {
  const query = new URLSearchParams();
  const days = periodDays[filters.period];
  if (days !== null) query.set('from', new Date(now.getTime() - days * 86_400_000).toISOString());
  if (filters.scheduleId) query.set('scheduleId', filters.scheduleId);
  if (filters.status) query.set('status', filters.status);
  if (filters.agentId) query.set('agentId', filters.agentId);
  if (filters.search) query.set('search', filters.search);
  query.set('page', String(filters.page));
  return query.toString();
}
