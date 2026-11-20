import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const summary = {
  kpi: { queued: 23, running: 3 },
  daily: [{ date: '2026-10-23', succeeded: 50, failed: 3 }],
  upcoming: [{ scheduleId: 's1', name: 'Проверка ЖК «Север»', at: '2026-10-23T17:00:00Z', modelCount: 38, module: 'loi' }],
  recentErrors: [{ at: '2026-10-23T11:22:00Z', modelName: 'ЖК-Юг_ВК.rvt', note: 'NWC: вид не найден', agentName: 'FARM-01', revitYear: 2022, jobId: 'j1', runId: 'r1' }],
};

const responses = { '/api/farm/summary': summary, '/api/runs': { items: [], total: 0 }, '/api/schedules': [], '/api/agents': [] };

describe('QueueSummary', () => {
  it('farm queue shows the chart, upcoming runs and recent errors', async () => {
    renderApp('/farm/queue', responses);

    expect(await screen.findByText('Модели по дням (14 дней)')).toBeTruthy();
    expect((await screen.findByRole('link', { name: 'Проверка ЖК «Север»' })).getAttribute('href')).toBe('/loi/schedules/s1');
    expect(screen.getByRole('link', { name: 'ЖК-Юг_ВК.rvt' }).getAttribute('href')).toBe('/farm/queue/runs/r1?job=j1');
    expect(screen.getByText(/23 в очереди · 3 в работе/)).toBeTruthy();
  });

  it('module history has no summary', async () => {
    renderApp('/export/history', responses);

    await screen.findByText('Запусков нет');
    expect(screen.queryByText('Модели по дням (14 дней)')).toBeNull();
  });
});
