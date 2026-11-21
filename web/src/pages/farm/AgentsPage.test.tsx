import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const agents = [
  {
    id: 'a1', machineName: 'BIM-PC-01', ip: null, agentVersion: '2.5.0', totalRamMb: 65536, state: 'Online', isDraining: false,
    lastSeenAt: new Date().toISOString(), cpuPercent: 64, ramUsedMb: 41000, revits: [{ year: 2022, hasNwcExporter: true }],
    limits: [{ year: 2022, maxInstances: 1 }], isOutdated: false,
    instances: [{ slot: 1, year: 2022, pid: 1, state: 'Busy', jobId: 'j9', stage: 'Экспорт NWC', stageSince: new Date().toISOString(), modelName: 'ЖК-Север_АР_К1.rvt' }],
  },
];

const operator = { id: 'u2', login: 'op', displayName: 'Координатор', role: 'Operator', isDisabled: false, lastLoginAt: null };

describe('AgentsPage', () => {
  it('keeps "Отменить" and "Убить" for a coordinator: the overview only shows what Revit does', async () => {
    renderApp('/farm/agents', { '/api/agents': agents, '/api/settings': { silenceTimeoutMinutes: 10 }, '/api/auth/me': operator });

    const main = await screen.findByRole('main');
    expect(await within(main).findByText(/Обрабатывает ЖК-Север_АР_К1\.rvt/)).toBeTruthy();
    expect(within(main).getByRole('button', { name: 'Отменить' })).toBeTruthy();
    expect(within(main).getByRole('button', { name: 'Убить' })).toBeTruthy();
  });
});
