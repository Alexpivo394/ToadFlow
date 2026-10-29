import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';

const setAr = { id: 'set-ar', discipline: 'АР', name: 'Все стены', anyOf: [{ all: [{ property: { kind: 'category' }, operator: 'equals', value: 'OST_Walls' }] }] };
const draft = {
  configuration: { sets: [setAr], matrix: [{ disciplineA: 'АР', disciplineB: 'ОВ', enabled: true, toleranceMm: 20 }], exclusions: [], ignoreConnectedMep: true },
  disciplines: ['АР', 'ОВ'],
  problems: [],
};
const part = {
  id: 'p1', name: 'Полковая, корпус 1', object: 'Полковая', part: 'корпус 1', updatedAt: '2026-10-28T10:00:00Z',
  models: [
    { modelId: 'm1', name: 'POL_K1_AR.rvt', modelPath: 'RSN://rs/POL_K1_AR.rvt', parsedDiscipline: 'АР', disciplineOverride: null, discipline: 'АР', latest: null },
    { modelId: 'm2', name: 'POL_K1_EL.rvt', modelPath: 'RSN://rs/POL_K1_EL.rvt', parsedDiscipline: null, disciplineOverride: 'ОВ', discipline: 'ОВ', latest: null },
  ],
};
const responses = {
  '/api/clash/parts/p1': part,
  '/api/clash/parts/p1/draft': draft,
  '/api/clash/catalog': { categories: [{ key: 'OST_Walls', count: 10 }], parameters: [], families: [], types: [], levels: [], worksets: [] },
  '/api/clash/templates': [],
  '/api/clash/models': [],
  '/api/clash/parts/p1/preview': { matched: 120, total: 300, sample: [] },
};

type FetchMock = { mock: { calls: [string, RequestInit?][] }; mockImplementation: (f: (url: string, init?: RequestInit) => Promise<Response>) => void };

describe('PartEditor', () => {
  it('PartEditor_ShowsModelsWithDisciplines', async () => {
    renderApp('/clash/parts/p1', responses);

    expect(await screen.findByText('Полковая, корпус 1')).toBeTruthy();
    expect(screen.getByText('POL_K1_AR.rvt')).toBeTruthy();
    expect(screen.getByText('не распознан')).toBeTruthy();
    expect(screen.getByDisplayValue('ОВ')).toBeTruthy(); // поправка раздела
  });

  it('SetEditor_AddConditionAndPreviewCount', async () => {
    renderApp('/clash/parts/p1', responses);

    fireEvent.click(await screen.findByRole('tab', { name: 'Наборы' }));
    expect(await screen.findByDisplayValue('Все стены')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '+ условие' }));
    expect(screen.getAllByRole('button', { name: 'Удалить условие' })).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: 'Сколько элементов попало' }));

    expect(await screen.findByText('Попало 120 из 300')).toBeTruthy();
    const calls = (fetch as unknown as FetchMock).mock.calls;
    const preview = calls.find(([url]) => url === '/api/clash/parts/p1/preview')!;
    expect(JSON.parse(String(preview[1]!.body)).sets).toHaveLength(1);
  });

  it('MatrixEditor_TogglesCellAndTolerance', async () => {
    renderApp('/clash/parts/p1', responses);

    fireEvent.click(await screen.findByRole('tab', { name: 'Матрица' }));
    const tolerance = await screen.findByLabelText('Допуск АР × ОВ');
    fireEvent.change(tolerance, { target: { value: '35' } });
    fireEvent.click(screen.getByLabelText('Проверять ОВ × ОВ'));
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить черновик' }));

    await waitFor(() => expect((fetch as unknown as FetchMock).mock.calls.some(([url, init]) => url === '/api/clash/parts/p1/draft' && init?.method === 'PUT')).toBe(true));
    const put = (fetch as unknown as FetchMock).mock.calls.find(([url, init]) => url === '/api/clash/parts/p1/draft' && init?.method === 'PUT')!;
    const matrix = JSON.parse(String(put[1]!.body)).matrix;
    expect(matrix).toContainEqual({ disciplineA: 'АР', disciplineB: 'ОВ', enabled: true, toleranceMm: 35 });
    expect(matrix).toContainEqual({ disciplineA: 'ОВ', disciplineB: 'ОВ', enabled: true, toleranceMm: 20 });
  });

  it('PartEditor_SaveShowsServerProblems', async () => {
    renderApp('/clash/parts/p1', responses);
    const original = (fetch as unknown as { getMockImplementation: () => (url: string, init?: RequestInit) => Promise<Response> }).getMockImplementation();
    (fetch as unknown as FetchMock).mockImplementation((url, init) =>
      url === '/api/clash/parts/p1/draft' && init?.method === 'PUT'
        ? Promise.resolve(new Response(JSON.stringify({ error: 'Набор «Все стены» (АР): у условия не задано значение; Матрица «АР» × «ВК»: клетка задана дважды', problems: [] }), { status: 400 }))
        : original(url, init));

    fireEvent.click(await screen.findByRole('button', { name: 'Сохранить черновик' }));

    expect(await screen.findByText('Набор «Все стены» (АР): у условия не задано значение')).toBeTruthy();
  });

  it('PartEditor_ReadOnlyForViewer', async () => {
    renderApp('/clash/parts/p1', {
      ...responses,
      '/api/auth/me': { id: 'u2', login: 'v', displayName: 'Наблюдатель', role: 'Viewer', isDisabled: false, lastLoginAt: null },
    });

    expect(await screen.findByText('Полковая, корпус 1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сохранить черновик' })).toBeNull();
    fireEvent.click(screen.getByRole('tab', { name: 'Наборы' }));
    expect(screen.queryByRole('button', { name: '+ условие' })).toBeNull();
  });
});


vi.setConfig({ testTimeout: 15_000 });
