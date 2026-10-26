import type { LoiCheckSource } from '../../api/types';

export type AnalyticsPeriod = '30d' | '90d' | '365d' | 'all';

/** Фильтры аналитики живут в адресе страницы: ссылкой на срез можно поделиться. */
export interface AnalyticsFilterState {
  period: AnalyticsPeriod;
  customer?: string;
  object?: string;
  discipline?: string;
  part?: string;
  profile?: string;
  source?: LoiCheckSource;
  search?: string;
  /** Свои поля справочника: имя → значение (или «(нет)»). */
  fields?: Record<string, string>;
}

const periodDays: Record<AnalyticsPeriod, number | null> = { '30d': 30, '90d': 90, '365d': 365, all: null };

const keys = ['customer', 'object', 'discipline', 'part', 'profile', 'source', 'search'] as const;

export function fromSearchParams(params: URLSearchParams): AnalyticsFilterState {
  const period = params.get('period');
  const state: AnalyticsFilterState = { period: period && period in periodDays ? (period as AnalyticsPeriod) : '90d' };
  for (const key of keys) {
    const value = params.get(key);
    if (value) (state as unknown as Record<string, string>)[key] = value;
  }
  const fields = parseFields(params.getAll('field'));
  if (Object.keys(fields).length > 0) state.fields = fields;
  return state;
}

/** Свои поля в адресе и в запросе к API — повторяющийся параметр field=Имя=Значение. */
function parseFields(values: string[]): Record<string, string> {
  const fields: Record<string, string> = {};
  for (const value of values) {
    const cut = value.indexOf('=');
    if (cut > 0 && cut < value.length - 1) fields[value.slice(0, cut)] = value.slice(cut + 1);
  }
  return fields;
}

function appendFields(params: URLSearchParams, fields: Record<string, string> | undefined) {
  for (const [name, value] of Object.entries(fields ?? {})) if (value) params.append('field', `${name}=${value}`);
}

export function toSearchParams(state: AnalyticsFilterState): URLSearchParams {
  const params = new URLSearchParams();
  if (state.period !== '90d') params.set('period', state.period);
  for (const key of keys) if (state[key]) params.set(key, String(state[key]));
  appendFields(params, state.fields);
  return params;
}

/**
 * Строка запроса к API: период превращается в «с начала такого-то дня». Начало дня, а не текущий момент: строка — часть
 * ключа запроса, и с миллисекундами она менялась бы на каждом рендере, а запрос перезапускался бы без конца.
 */
export function toApiQuery(state: AnalyticsFilterState, now: Date = new Date()): string {
  const query = new URLSearchParams();
  const days = periodDays[state.period];
  if (days !== null) query.set('from', new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - days)).toISOString());
  for (const key of keys) if (state[key]) query.set(key, String(state[key]));
  appendFields(query, state.fields);
  return query.toString();
}

/** Значение фильтра «поле не распознано» — так его понимает сервер. */
export const noneValue = '(нет)';

/** Цвет доли: от 95 % — зелёный, от 70 % — жёлтый, ниже — красный. */
export function shareColor(share: number | null | undefined): string {
  if (share == null) return 'gray';
  return share >= 0.95 ? 'green' : share >= 0.7 ? 'yellow' : 'red';
}

/** Доля в проценты для графиков: 0,8523 → 85,2. */
export const toPercent = (share: number | null | undefined) => (share == null ? null : Math.round(share * 1000) / 10);
