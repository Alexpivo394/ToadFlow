import { describe, expect, it } from 'vitest';
import type { InstanceStatus } from '../../api/types';
import { describeInstance } from './agentStatus';

const now = new Date('2026-10-23T12:00:00Z');
const base: InstanceStatus = { slot: 1, year: 2022, pid: 11840, state: 'Idle', jobId: null, stage: null, stageSince: null, modelName: null };

describe('describeInstance', () => {
  it('busy shows model, stage and elapsed', () =>
    expect(
      describeInstance(
        { ...base, state: 'Busy', jobId: 'j', modelName: 'ЖК-Север_АР_К1.rvt', stage: 'Экспорт NWC', stageSince: '2026-10-23T11:56:19Z' },
        10,
        now,
      ),
    ).toEqual({ tone: 'busy', text: 'Выгружает ЖК-Север_АР_К1.rvt · Экспорт NWC · 3:41' }));

  it('unresponsive counts down to restart', () =>
    expect(
      describeInstance({ ...base, state: 'Unresponsive', jobId: 'j', modelName: 'ЖК-Юг_КР.rvt', stageSince: '2026-10-23T11:54:00Z' }, 10, now),
    ).toEqual({ tone: 'warn', text: 'Не отвечает 6 мин · ЖК-Юг_КР.rvt — будет перезапущен через 4 мин' }));

  it('unresponsive past the limit never goes negative', () =>
    expect(describeInstance({ ...base, state: 'Unresponsive', stageSince: '2026-10-23T11:40:00Z' }, 10, now).text).toContain('через 0 мин'));

  it('idle', () => expect(describeInstance(base, 10, now)).toEqual({ tone: 'idle', text: 'Свободен' }));

  it('starting', () => expect(describeInstance({ ...base, state: 'Starting' }, 10, now).text).toBe('Запускается'));
});
