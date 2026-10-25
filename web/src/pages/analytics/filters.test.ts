import { describe, expect, it } from 'vitest';
import { fromSearchParams, shareColor, toApiQuery, toPercent, toSearchParams } from './filters';

describe('analytics filters', () => {
  it('round-trips through the address', () => {
    const state = { period: '365d' as const, object: 'Полковая', discipline: 'АР', source: 'Manual' as const };
    expect(fromSearchParams(toSearchParams(state))).toEqual(state);
    expect(toSearchParams({ period: '90d' }).toString()).toBe('');
  });

  it('period becomes from', () => {
    const query = new URLSearchParams(toApiQuery({ period: '30d', customer: 'ГК Пионер' }, new Date('2026-10-31T00:00:00Z')));
    expect(query.get('from')).toBe('2026-10-01T00:00:00.000Z');
    // в течение дня строка не меняется — иначе ключ запроса новый на каждом рендере
    expect(toApiQuery({ period: '30d' }, new Date('2026-10-31T17:45:12.345Z'))).toBe(toApiQuery({ period: '30d' }, new Date('2026-10-31T00:00:00Z')));
    expect(query.get('customer')).toBe('ГК Пионер');
    expect(new URLSearchParams(toApiQuery({ period: 'all' })).has('from')).toBe(false);
  });

  it('colors and percents', () => {
    expect(shareColor(0.96)).toBe('green');
    expect(shareColor(0.8)).toBe('yellow');
    expect(shareColor(0.2)).toBe('red');
    expect(shareColor(null)).toBe('gray');
    expect(toPercent(0.8523)).toBe(85.2);
  });
});
