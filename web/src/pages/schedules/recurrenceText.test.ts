import { describe, expect, it } from 'vitest';
import { formatDateTime } from '../../lib/format';
import { describeRecurrence } from './recurrenceText';

const rule = { recurrenceKind: 'Weekly' as const, daysOfWeekMask: 0b0011111, timeOfDay: '20:00:00', intervalHours: null, onceAt: null };

describe('describeRecurrence', () => {
  it('weekdays', () => expect(describeRecurrence(rule)).toBe('Пн–Пт 20:00'));
  it('every N hours', () => expect(describeRecurrence({ ...rule, recurrenceKind: 'EveryNHours', intervalHours: 4 })).toBe('каждые 4 ч'));
  it('selected days', () => expect(describeRecurrence({ ...rule, daysOfWeekMask: 0b0010101, timeOfDay: '06:00:00' })).toBe('Пн, Ср, Пт 06:00'));
  it('every day', () => expect(describeRecurrence({ ...rule, daysOfWeekMask: 0b1111111 })).toBe('ежедневно 20:00'));
  it('once', () => {
    const onceAt = '2026-10-24T03:00:00Z';
    expect(describeRecurrence({ ...rule, recurrenceKind: 'Once', onceAt })).toBe(`один раз ${formatDateTime(onceAt)}`);
  });
});
