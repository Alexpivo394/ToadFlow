import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const run = {
  id: 'r1', scheduleId: 's1', name: 'ЖК «Север» — все', createdAt: '2026-10-23T08:12:00Z', startedAt: '2026-10-23T08:12:05Z', finishedAt: '2026-10-23T10:04:00Z',
  state: 'Completed', triggeredBy: 'И. Петров', counts: { succeeded: 35, warning: 2, failed: 1, queued: 0, running: 0, cancelled: 0, total: 38 },
  durationSeconds: 6720, warning: null,
};

const job = {
  id: 'j1', runId: 'r1', runName: run.name, moduleKind: 'export', modelName: 'ОВ_К1.rvt', modelPath: 'RSN://rs22/ЖК/ОВ_К1.rvt', status: 'Failed',
  revitYear: 2022, requiresNwc: true, agentId: 'a1', agentName: 'FARM-02', slot: 1, attempt: 2, maxAttempts: 2, createdAt: run.createdAt,
  startedAt: '2026-10-23T08:48:02Z', finishedAt: '2026-10-23T09:18:04Z', durationSeconds: 1802, note: 'Revit не отвечал, убит по таймауту',
  result: null, waitingForRevit: null, cancelRequested: false,
};

const waiting = { ...job, id: 'j2', modelName: 'КР_К1.rvt', status: 'Queued', note: null, waitingForRevit: 2024, durationSeconds: null };

describe('HistoryPage', () => {
  it('lists runs with who started them', async () => {
    renderApp('/history', { '/api/runs': { items: [run], total: 1 }, '/api/schedules': [], '/api/agents': [] });

    expect(await screen.findByText('ЖК «Север» — все')).toBeTruthy();
    expect(screen.getByText('И. Петров (вручную)')).toBeTruthy();
    expect(screen.getByText('1 ч 52 мин')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Перезапустить упавшие' })).toBeTruthy();
  });

  it('run details expand a model log', async () => {
    renderApp('/history/runs/r1', {
      '/api/runs/r1': { run, jobs: [job, waiting] },
      '/api/jobs/j1/log': [{ at: '2026-10-23T08:48:02Z', stage: 'Открытие', text: 'открытие модели' }],
    });

    expect(await screen.findByText('ОВ_К1.rvt')).toBeTruthy();
    expect(screen.getByText('ждёт Revit 2024')).toBeTruthy();
    fireEvent.click(screen.getByText('ОВ_К1.rvt'));
    expect(await screen.findByText(/открытие модели/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Скачать лог' })).toBeTruthy();
  });

  it('system events tab', async () => {
    renderApp('/history/events', {
      '/api/events': [{ id: 5, at: '2026-10-23T09:15:00Z', actorKind: 'User', actorName: 'И. Петров', kind: 'InstanceKilled', text: 'Вручную убит Revit #2 на BIM-FARM-01' }],
    });

    expect(await screen.findByText('Вручную убит Revit #2 на BIM-FARM-01')).toBeTruthy();
  });
});

describe('HistoryPage for LOI', () => {
  it('LOI jobs show completeness and fill rate per rule', async () => {
    const checked = {
      ...job, id: 'j3', moduleKind: 'loi', modelName: 'АР_К1.rvt', status: 'Succeeded', note: null, requiresNwc: false,
      result: {
        elements: 120, completeElements: 102, completeness: 0.85, values: 240, filled: 228, empty: 10, missing: 2, fillRate: 0.95,
        reportPath: '\\nas\loi\АР_К1_LOIReport.xlsx', rules: [{ title: 'Стены', elements: 120, completeElements: 102, completeness: 0.85, fillRate: 0.95 }],
      },
    };
    renderApp('/history/runs/r1', { '/api/runs/r1': { run, jobs: [checked] }, '/api/jobs/j3/log': [] });

    expect(await screen.findByText('Полнота')).toBeTruthy();
    expect(screen.getAllByText('85,0 %').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByText('АР_К1.rvt'));
    expect(await screen.findByText('Стены')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Скопировать путь к отчёту' })).toBeTruthy();
  });
});
