import { describe, expect, it } from 'vitest';
import { formatBytes, formatDuration, formatRelative, pluralRu } from './format';

describe('formatDuration', () => {
  it('minutes and seconds under an hour', () => expect(formatDuration(252)).toBe('4:12'));
  it('hours and minutes from an hour', () => expect(formatDuration(6720)).toBe('1 ч 52 мин'));
  it('pads seconds', () => expect(formatDuration(61)).toBe('1:01'));
  it('empty for missing', () => expect(formatDuration(null)).toBe('—'));
});

describe('formatBytes', () => {
  it('megabytes', () => expect(formatBytes(432013312)).toBe('412 МБ'));
  it('gigabytes', () => expect(formatBytes(2.5 * 1024 ** 3)).toBe('2,5 ГБ'));
  it('kilobytes', () => expect(formatBytes(2048)).toBe('2 КБ'));
});

describe('pluralRu', () => {
  const forms: [string, string, string] = ['модель', 'модели', 'моделей'];
  it('one', () => expect(pluralRu(1, forms)).toBe('модель'));
  it('few', () => expect(pluralRu(3, forms)).toBe('модели'));
  it('eleven is many', () => expect(pluralRu(11, forms)).toBe('моделей'));
  it('twenty one is one', () => expect(pluralRu(21, forms)).toBe('модель'));
});

describe('formatRelative', () => {
  const now = new Date('2026-10-23T12:00:00Z');
  it('seconds', () => expect(formatRelative('2026-10-23T11:59:57Z', now)).toBe('3 с назад'));
  it('minutes', () => expect(formatRelative('2026-10-23T11:54:00Z', now)).toBe('6 мин назад'));
  it('hours', () => expect(formatRelative('2026-10-23T10:00:00Z', now)).toBe('2 ч назад'));
});
