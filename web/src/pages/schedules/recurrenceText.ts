import type { RecurrenceKind } from '../../api/types';
import { formatDateTime } from '../../lib/format';

export const dayNames = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

interface Rule {
  recurrenceKind: RecurrenceKind;
  daysOfWeekMask: number;
  timeOfDay: string;
  intervalHours: number | null;
  onceAt: string | null;
}

/** «Пн–Пт 20:00», «каждые 4 ч», «один раз 24.10 06:00». */
export function describeRecurrence(rule: Rule): string {
  if (rule.recurrenceKind === 'EveryNHours') return `каждые ${rule.intervalHours ?? '?'} ч`;
  if (rule.recurrenceKind === 'Once') return `один раз ${formatDateTime(rule.onceAt)}`;
  return `${describeDays(rule.daysOfWeekMask)} ${rule.timeOfDay.slice(0, 5)}`;
}

export function describeDays(mask: number): string {
  const days = dayNames.filter((_, i) => (mask & (1 << i)) !== 0);
  if (days.length === 7) return 'ежедневно';
  if (mask === 0b0011111) return 'Пн–Пт';
  return days.join(', ') || '—';
}
