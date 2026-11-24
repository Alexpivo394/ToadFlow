import { describe, expect, it } from 'vitest';
import { formatChange, formatDuration, formatHours, formatMemory } from './format';

describe('usage format', () => {
  it('formats durations the Russian way', () => {
    expect(formatDuration(42_000)).toBe('42 с');
    expect(formatDuration(845_000)).toBe('14 мин 05 с');
    expect(formatDuration(4_200_000)).toBe('1 ч 10 мин');
    expect(formatDuration(null)).toBe('—');
  });

  it('formats hours with a decimal comma', () => {
    expect(formatHours(45_000_000)).toBe('12,5 ч');
    expect(formatHours(0)).toBe('0 ч');
  });

  it('change to the previous period, none without a previous value', () => {
    expect(formatChange(112, 100)).toBe('+12 %');
    expect(formatChange(95, 100)).toBe('−5 %');
    expect(formatChange(5, 0)).toBeNull();
  });

  it('memory in gigabytes', () => {
    expect(formatMemory(32768)).toBe('32 ГБ');
    expect(formatMemory(512)).toBe('512 МБ');
  });
});
