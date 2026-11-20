import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { pickOption, renderApp } from '../../test/renderApp';

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
    renderApp('/farm/queue', { '/api/runs': { items: [run], total: 1 }, '/api/schedules': [], '/api/agents': [] });

    expect(await screen.findByText('ЖК «Север» — все')).toBeTruthy();
    expect(screen.getByText('И. Петров (вручную)')).toBeTruthy();
    expect(screen.getByText('1 ч 52 мин')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Перезапустить упавшие' })).toBeTruthy();
  });

  it('run details expand a model log', async () => {
    renderApp('/farm/queue/runs/r1', {
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
    renderApp('/farm/queue/runs/r1', { '/api/runs/r1': { run, jobs: [checked] }, '/api/jobs/j3/log': [] });

    expect(await screen.findByText('Полнота')).toBeTruthy();
    expect(screen.getAllByText('85,0 %').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByText('АР_К1.rvt'));
    expect(await screen.findByText('Стены')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Скопировать путь к отчёту' })).toBeTruthy();
  });
});

describe('HistoryPage: clash extraction', () => {
  it('History_ClashExtract_ShowsRunsItStarted', async () => {
    const clashJob = { ...job, id: 'j5', moduleKind: 'clash-extract', status: 'Succeeded', note: null };
    renderApp('/farm/queue/runs/r1', {
      '/api/runs/r1': { run, jobs: [clashJob] },
      '/api/clash/extract-runs/r1/engine-runs': [
        { runId: 'e1', partId: 'p1', partName: 'Корпус 1', state: 'Succeeded', new: 12, error: null },
        { runId: 'e2', partId: 'p2', partName: 'Корпус 2', state: 'Queued', new: null, error: null },
      ],
    });

    const line = await screen.findByTestId('clash-runs');
    expect(line.textContent).toBe('Прогоны:Корпус 1— готово (новых 12)·Корпус 2— в очереди');
    expect(screen.getByRole('link', { name: 'Корпус 1' }).getAttribute('href')).toBe('/clash/checks/p1');
  });
});

describe('History inside a module and the farm queue', () => {
  it('module history requests only its module and links runs inside the module', async () => {
    const urls: string[] = [];
    renderApp('/loi/history', {
      '/api/runs': (url: string) => {
        urls.push(url);
        return { items: [run], total: 1 };
      },
      '/api/runs/r1': { run, jobs: [job] },
      '/api/schedules': [],
      '/api/agents': [],
    });

    fireEvent.click(await screen.findByText('ЖК «Север» — все'));

    expect((await screen.findByRole('link', { name: '← К запускам' })).getAttribute('href')).toBe('/loi/history');
    expect(urls[0]).toContain('module=loi');
  });

  it('farm queue shows all modules and filters by module', async () => {
    const urls: string[] = [];
    renderApp('/farm/queue', {
      '/api/runs': (url: string) => {
        urls.push(url);
        return { items: [run], total: 1 };
      },
      '/api/schedules': [],
      '/api/agents': [],
    });

    expect(await screen.findByText('ЖК «Север» — все')).toBeTruthy();
    expect(urls[0]).not.toContain('module=');
    await pickOption(screen.getByPlaceholderText('Все модули'), 'Проверка LOI');
    await waitFor(() => expect(urls[urls.length - 1]).toContain('module=loi'));
  });
});

describe('History sorting', () => {
  it('history sends sort and dir to the server', async () => {
    const urls: string[] = [];
    renderApp('/farm/queue', {
      '/api/runs': (url: string) => {
        urls.push(url);
        return { items: [run], total: 1 };
      },
      '/api/schedules': [],
      '/api/agents': [],
    });
    await screen.findByText('ЖК «Север» — все');

    fireEvent.click(screen.getByRole('button', { name: /Длительность/ }));
    await waitFor(() => expect(urls[urls.length - 1]).toContain('sort=duration'));
    fireEvent.click(screen.getByRole('button', { name: /Длительность/ }));
    await waitFor(() => expect(urls[urls.length - 1]).toContain('dir=desc'));
  });
});
