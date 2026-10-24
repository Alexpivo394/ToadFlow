import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const dashboard = {
  kpi: { exported24h: 142, failed24h: 6, failedPercent: 4.2, queued: 23, running: 3, avgDurationSeconds24h: 252, agentsOnline: 2, agentsTotal: 3 },
  daily: [{ date: '2026-10-23', succeeded: 50, failed: 3 }],
  upcoming: [{ scheduleId: 's1', name: 'ЖК «Север» — все', at: '2026-10-23T17:00:00Z', modelCount: 38 }],
  recentErrors: [{ at: '2026-10-23T11:22:00Z', modelName: 'ЖК-Юг_ВК.rvt', note: 'NWC: вид «Navisworks» не найден', agentName: 'FARM-01', revitYear: 2022, jobId: 'j1', runId: 'r1' }],
};

const agents = [
  {
    id: 'a1', machineName: 'BIM-FARM-01', ip: null, agentVersion: '1.0.3', totalRamMb: 65536, state: 'Online', isDraining: false,
    lastSeenAt: new Date().toISOString(), cpuPercent: 64, ramUsedMb: 41000, revits: [{ year: 2022, hasNwcExporter: true }],
    limits: [{ year: 2022, maxInstances: 1 }], isOutdated: false,
    instances: [{ slot: 1, year: 2022, pid: 1, state: 'Busy', jobId: 'j9', stage: 'Экспорт NWC', stageSince: new Date().toISOString(), modelName: 'ЖК-Север_АР_К1.rvt' }],
  },
];

describe('DashboardPage', () => {
  it('renders KPIs, agents, upcoming runs and errors', async () => {
    renderApp('/', { '/api/dashboard': dashboard, '/api/agents': agents, '/api/settings': { silenceTimeoutMinutes: 10 } });

    expect(await screen.findByText('142')).toBeTruthy();
    expect(await screen.findByText(/Обрабатывает ЖК-Север_АР_К1\.rvt/)).toBeTruthy();
    expect(await screen.findByText('ЖК «Север» — все')).toBeTruthy();
    expect(await screen.findByText('ЖК-Юг_ВК.rvt')).toBeTruthy();
    expect(screen.getByText('BIM-FARM-01')).toBeTruthy();
  });

  it('shows empty states without agents and schedules', async () => {
    renderApp('/', { '/api/dashboard': { ...dashboard, upcoming: [], recentErrors: [] }, '/api/agents': [], '/api/settings': { silenceTimeoutMinutes: 10 } });

    expect(await screen.findByText(/Агенты не подключены/)).toBeTruthy();
    expect(await screen.findByText(/Расписаний нет/)).toBeTruthy();
  });
});
