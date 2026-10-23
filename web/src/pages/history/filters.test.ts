import { describe, expect, it } from 'vitest';
import { fromSearchParams, toApiQuery, toSearchParams } from './filters';

describe('history filters', () => {
  it('round trip', () => {
    const filters = { period: '7d' as const, scheduleId: 's1', status: 'Failed' as const, agentId: 'a1', search: 'ЖК', page: 2 };

    expect(fromSearchParams(toSearchParams(filters))).toEqual(filters);
  });

  it('empty fields are not in the URL', () => {
    expect(toSearchParams({ period: '7d', search: '', page: 1 }).toString()).toBe('');
  });

  it('defaults to seven days, first page', () => {
    expect(fromSearchParams(new URLSearchParams())).toEqual({ period: '7d', page: 1 });
  });

  it('api query turns period into from', () => {
    const now = new Date('2026-10-23T12:00:00Z');
    const query = new URLSearchParams(toApiQuery({ period: '1d', status: 'Failed', page: 3 }, now));

    expect(query.get('from')).toBe('2026-10-22T12:00:00.000Z');
    expect(query.get('status')).toBe('Failed');
    expect(query.get('page')).toBe('3');
  });

  it('api query without period limit', () => {
    expect(new URLSearchParams(toApiQuery({ period: 'all', page: 1 })).get('from')).toBeNull();
  });
});
