import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';

const part = { id: 'p1', name: 'Полковая, корпус 1', object: null, part: null, updatedAt: '2026-10-28T10:00:00Z', models: [] };
const draft = { configuration: { sets: [], matrix: [], exclusions: [], ignoreConnectedMep: true }, disciplines: ['АР', 'ОВ'], problems: [] };
const hit = (n: number) => ({
  number: n, depthMm: 40 + n, x: 0, y: 0, z: 0,
  modelA: 'a', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', setNamesA: ['Стены'], categoryA: 'OST_Walls',
  familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'b', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', setNamesB: ['Воздуховоды'],
  categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
});
const base = {
  '/api/clash/parts/p1': part,
  '/api/clash/parts/p1/draft': draft,
  '/api/clash/catalog': { categories: [], parameters: [], families: [], types: [], levels: [], worksets: [] },
  '/api/clash/templates': [],
  '/api/clash/models': [],
  '/api/clash/parts/p1/draft/summary': {
    total: 120,
    byDisciplines: [{ disciplinePair: 'АР × ОВ', count: 100, enabled: true, toleranceMm: 20 }, { disciplinePair: 'ОВ × ОВ', count: 20, enabled: true, toleranceMm: 20 }],
    bySets: [{ setA: 's1', setAName: 'Стены', setB: 's2', setBName: 'Воздуховоды', count: 100 }],
  },
  '/api/clash/parts/p1/draft/hits': { total: 120, items: [hit(1), hit(2)] },
};
const succeeded = {
  runId: 'r1', state: 'Succeeded', stage: null, hitCount: 120, error: null, createdAt: '2026-10-28T10:00:00Z', finishedAt: '2026-10-28T10:02:24Z',
  stats: { elements: 173000, triangles: 69600000, candidatePairs: 5000000, loadSeconds: 30, broadSeconds: 4, narrowSeconds: 110, peakManagedBytes: 2e9, peakWorkingSetBytes: 4.2e9 },
};

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

async function openDraftTab(responses: Record<string, unknown>) {
  renderApp('/clash/parts/p1', responses);
  fireEvent.click(await screen.findByRole('tab', { name: 'Черновик' }));
}

describe('Draft run', () => {
  it('DraftRun_StartsAndShowsStage', async () => {
    await openDraftTab({
      ...base,
      '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Running', stage: 'Поиск пересечений', hitCount: null, finishedAt: null, stats: null },
    });

    expect(await screen.findByText('Поиск пересечений')).toBeTruthy();
    expect(screen.getByText('выполняется')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Прогнать' }));

    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/draft/run' && init?.method === 'POST')).toBe(true));
    expect(screen.getByRole('button', { name: 'Отменить' })).toBeTruthy();
  });

  it('DraftRun_FailedShowsError', async () => {
    await openDraftTab({ ...base, '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Failed', error: 'слепок модели повреждён', stats: null } });

    expect(await screen.findByText('слепок модели повреждён')).toBeTruthy();
  });

  it('DraftResults_SummaryClickFiltersList', async () => {
    await openDraftTab({ ...base, '/api/clash/parts/p1/draft/run': succeeded });

    expect(await screen.findByText('Коллизий: 120')).toBeTruthy();
    expect(screen.getByText(/поиск 1:54/)).toBeTruthy();
    expect(screen.getAllByText(/POL_K1_AR.rvt · ID 1001/).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: /ОВ × ОВ/ }));

    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/clash/parts/p1/draft/hits?') && new URLSearchParams(url.split('?')[1]).get('disciplines') === 'ОВ × ОВ')).toBe(true));
  });

  it('DraftResults_PagesThroughHits', async () => {
    await openDraftTab({ ...base, '/api/clash/parts/p1/draft/run': succeeded });

    fireEvent.click(await screen.findByRole('button', { name: '2' }));

    await waitFor(() => expect(calls().some(([url]) => url.includes('/draft/hits?') && url.includes('page=2'))).toBe(true));
  });
});

describe('Draft without runs', () => {
  it('DraftRun_NoRunYet_ShowsStartWithoutQueryError', async () => {
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    await openDraftTab({ ...base, '/api/clash/parts/p1/draft/run': null, '/api/clash/parts/p1/draft/summary': null });

    expect(await screen.findByRole('button', { name: 'Прогнать' })).toBeTruthy();
    expect(await screen.findByText(/Результатов пока нет/)).toBeTruthy();
    await new Promise((r) => setTimeout(r, 50));
    expect(errors.mock.calls.filter((c) => String(c[0]).includes('undefined'))).toEqual([]);
    errors.mockRestore();
  });
});
