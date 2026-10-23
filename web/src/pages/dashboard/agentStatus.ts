import type { InstanceStatus } from '../../api/types';
import { formatDuration } from '../../lib/format';

export type InstanceTone = 'busy' | 'idle' | 'warn' | 'down';

/** Одна строка о состоянии экземпляра Revit для дашборда. */
export function describeInstance(
  instance: InstanceStatus,
  silenceTimeoutMinutes: number,
  now: Date = new Date(),
): { tone: InstanceTone; text: string } {
  const since = instance.stageSince ? (now.getTime() - new Date(instance.stageSince).getTime()) / 1000 : null;
  const model = instance.modelName ?? 'модель';

  switch (instance.state) {
    case 'Busy':
      return { tone: 'busy', text: ['Выгружает ' + model, instance.stage, formatDuration(since)].filter(Boolean).join(' · ') };
    case 'Unresponsive': {
      const silent = Math.floor((since ?? 0) / 60);
      const left = Math.max(0, silenceTimeoutMinutes - silent);
      return { tone: 'warn', text: `Не отвечает ${silent} мин · ${model} — будет перезапущен через ${left} мин` };
    }
    case 'Restarting':
      return { tone: 'warn', text: 'Перезапускается' };
    case 'Starting':
      return { tone: 'idle', text: 'Запускается' };
    default:
      return { tone: 'idle', text: 'Свободен' };
  }
}

export const toneColor: Record<InstanceTone, string> = { busy: 'blue', idle: 'gray', warn: 'orange', down: 'red' };
