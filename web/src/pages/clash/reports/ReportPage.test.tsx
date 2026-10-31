import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { navigateTo, renderApp } from '../../../test/renderApp';

vi.mock('../../../lib/clipboard', () => ({ copyText: vi.fn(async () => true) }));
vi.mock('../../../api/client', async (original) => ({ ...(await original<typeof import('../../../api/client')>()), downloadFile: vi.fn(async () => {}) }));

const { copyText } = await import('../../../lib/clipboard');
const { downloadFile } = await import('../../../api/client');

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const rowCalls = () => calls().filter(([url]) => url.startsWith('/api/clash/reports/r2/collisions')).map(([url]) => new URL(url, 'http://x').searchParams);

const collision = (n: number, extra: object = {}) => ({
  id: `c${n}`, number: n, status: 'Returned', assignee: 'Both', foundNow: true, depthMm: 40 + n, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', categoryA: 'OST_Walls', familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k-arov'], firstVersion: 1, lastFoundVersion: 2, suggestedPredecessorId: null, suggestedPredecessorNumber: null, imageVersion: 'v1', ...extra,
});

const summary = { found: 30, new: 12, active: 15, returned: 3, fixed: 4, goneByRules: 1, unchecked: 2, fake: 5, reusedPairs: 0 };

const report = (extra: object = {}) => ({
  run: { runId: 'r2', partId: 'p1', version: 2, finishedAt: '2026-10-31T09:05:00Z', trigger: 'manual', startedBy: 'Иванов', searchSeconds: 12.5 },
  check: { partId: 'p1', name: 'Корпус 1', object: 'Полковая', part: 'корпус 1', version: 2, lastRunAt: null, lastRun: null, open: 30, models: [] },
  summary,
  models: [
    { name: 'POL_K1_AR.rvt', discipline: 'АР', uploadedAt: '2026-10-31T08:00:00Z', unchecked: true },
    { name: 'POL_K1_OV.rvt', discipline: 'ОВ', uploadedAt: '2026-10-31T08:10:00Z', unchecked: false },
  ],
  pairs: [{ pair: 'АР × ОВ', open: 30, new: 12, returned: 3, fixed: 4, fake: 5 }],
  newerRunId: null,
  newerRunAt: null,
  ...extra,
});

const responses = (extra: Record<string, unknown> = {}) => ({
  '/api/clash/reports/r2': report(),
  '/api/clash/reports/r2/collisions': {
    total: 2,
    items: [
      { collision: collision(1), change: 'Returned', lastNote: 'Переносим трубу', lastNoteBy: 'Петров', lastNoteAt: '2026-10-31T10:00:00Z' },
      { collision: collision(2, { status: 'New' }), change: 'New', lastNote: null, lastNoteBy: null, lastNoteAt: null },
    ],
  },
  '/api/clash/reports/r2/chat': { text: 'Коллизии: Полковая · Корпус 1 (v2)' },
  '/api/clash/collisions/c1': { collision: collision(1), feed: [{ at: '2026-10-31T10:00:00Z', kind: 'Note', userName: 'Петров', text: 'Переносим трубу', fromStatus: null, toStatus: null, version: null, depthMm: null }] },
  ...extra,
});

const designer = { '/api/auth/me': { id: 'u2', login: 'ov', displayName: 'Проектировщик', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: ['ОВ'] } };

describe('Report page', () => {
  beforeEach(() => vi.clearAllMocks());

  it('ReportPage_HeaderTilesPairsAndRows', async () => {
    renderApp('/clash/reports/r2', responses());

    expect(await screen.findByRole('heading', { name: /Отчёт от 31\.10\.2026/ })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Корпус 1' }).getAttribute('href')).toBe('/clash/checks/p1');
    expect(screen.getByText(/вручную \(Иванов\)/)).toBeTruthy();
    const tiles = screen.getByTestId('report-tiles');
    expect(within(tiles).getByText('Новых').parentElement!.textContent).toContain('12');
    expect(within(tiles).getByText('Фиктивных').parentElement!.textContent).toContain('5');
    expect(screen.getByRole('button', { name: 'Пара АР × ОВ' })).toBeTruthy();
    expect(await screen.findByText('№ 1')).toBeTruthy();
    expect(screen.getAllByText('вернулась').length).toBeGreaterThan(0);
    expect(screen.getByText(/Переносим трубу/)).toBeTruthy();
    expect(rowCalls()[0].get('scope')).toBe('changes');
    expect(rowCalls()[0].get('pageSize')).toBe('50');
  });

  it('ReportPage_OldRun_ShowsNewerBanner', async () => {
    renderApp('/clash/reports/r2', responses({ '/api/clash/reports/r2': report({ newerRunId: 'r3', newerRunAt: '2026-11-01T09:00:00Z' }) }));

    expect(await screen.findByText(/Есть более новый прогон от 01\.11\.2026/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /Открыть новый отчёт/ }).getAttribute('href')).toBe('/clash/reports/r3?scope=changes');
  });

  it('ReportPage_NewerReportLink_KeepsFilters', async () => {
    renderApp('/clash/reports/r2?level=Этаж 1&q=17', responses({
      '/api/clash/reports/r2': report({ newerRunId: 'r3', newerRunAt: '2026-11-01T09:00:00Z' }),
      '/api/clash/reports/r3': report({ run: { ...report().run, runId: 'r3' } }),
      '/api/clash/reports/r3/collisions': { total: 0, items: [] },
    }));

    const link = await screen.findByRole('link', { name: /Открыть новый отчёт/ });
    const [path, query] = link.getAttribute('href')!.split('?');
    expect(path).toBe('/clash/reports/r3');
    expect(new URLSearchParams(query).get('level')).toBe('Этаж 1');
    expect(new URLSearchParams(query).get('q')).toBe('17');
    fireEvent.click(link);

    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/clash/reports/r3/collisions'))).toBe(true));
    await waitFor(() => expect((screen.getByPlaceholderText('Уровень') as HTMLInputElement).value).toBe('Этаж 1'));
    expect((screen.getByPlaceholderText('№, ID или UniqueId') as HTMLInputElement).value).toBe('17');
  });

  it('ReportPage_NavigateWithoutParams_ClearsFilterFields', async () => {
    renderApp('/clash/reports/r2?level=Этаж 1&q=17', responses({
      '/api/clash/reports/r3': report({ run: { ...report().run, runId: 'r3' } }),
      '/api/clash/reports/r3/collisions': { total: 0, items: [] },
    }));
    expect(((await screen.findByPlaceholderText('Уровень')) as HTMLInputElement).value).toBe('Этаж 1');

    navigateTo('/clash/reports/r3');

    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/clash/reports/r3/collisions'))).toBe(true));
    await waitFor(() => expect((screen.getByPlaceholderText('Уровень') as HTMLInputElement).value).toBe(''));
    expect((screen.getByPlaceholderText('№, ID или UniqueId') as HTMLInputElement).value).toBe('');
  });

  it('ReportPage_NavigateSameReportWithoutParams_ClearsFilterFields', async () => {
    renderApp('/clash/reports/r2?level=Этаж 1&q=17', responses());
    expect(((await screen.findByPlaceholderText('Уровень')) as HTMLInputElement).value).toBe('Этаж 1');

    navigateTo('/clash/reports/r2');

    await waitFor(() => expect((screen.getByPlaceholderText('Уровень') as HTMLInputElement).value).toBe(''));
    expect((screen.getByPlaceholderText('№, ID или UniqueId') as HTMLInputElement).value).toBe('');
  });

  it('ReportPage_PairClick_FiltersRows', async () => {
    renderApp('/clash/reports/r2', responses());

    fireEvent.click(await screen.findByRole('button', { name: 'Пара АР × ОВ' }));

    await waitFor(() => expect(rowCalls().some((p) => p.get('pair') === 'АР × ОВ')).toBe(true));
  });

  it('ReportPage_ScopeSwitch_RequestsOpen', async () => {
    renderApp('/clash/reports/r2', responses());

    fireEvent.click(await screen.findByText('Открытые сейчас'));

    await waitFor(() => expect(rowCalls().some((p) => p.get('scope') === 'open')).toBe(true));
  });

  it('ReportPage_Excel_DownloadsWithFiltersAndBaseUrl', async () => {
    renderApp('/clash/reports/r2?pair=' + encodeURIComponent('АР × ОВ'), responses());

    fireEvent.click(await screen.findByRole('button', { name: 'Excel' }));

    await waitFor(() => expect(downloadFile).toHaveBeenCalled());
    const [path, name] = vi.mocked(downloadFile).mock.calls[0];
    const url = new URL(path, 'http://x');
    expect(url.pathname).toBe('/api/clash/reports/r2/export.xlsx');
    expect(url.searchParams.get('scope')).toBe('changes');
    expect(url.searchParams.get('pair')).toBe('АР × ОВ');
    expect(url.searchParams.get('baseUrl')).toBe(window.location.origin);
    expect(name).toMatch(/\.xlsx$/);
  });

  it('ReportPage_CopyChat_OperatorOnly', async () => {
    const view = renderApp('/clash/reports/r2', responses(designer));
    expect(await screen.findByText('№ 1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Скопировать сообщение для чата' })).toBeNull();
    view.unmount();

    renderApp('/clash/reports/r2', responses());
    fireEvent.click(await screen.findByRole('button', { name: 'Скопировать сообщение для чата' }));

    await waitFor(() => expect(copyText).toHaveBeenCalledWith('Коллизии: Полковая · Корпус 1 (v2)'));
    const chat = calls().find(([url]) => url.startsWith('/api/clash/reports/r2/chat'))!;
    expect(new URL(chat[0], 'http://x').searchParams.get('baseUrl')).toBe(window.location.origin);
    expect(await screen.findByText('Скопировано')).toBeTruthy();
  });

  it('ReportPage_RowOpensDrawerWithCardAndLink3D', async () => {
    renderApp('/clash/reports/r2', responses());

    fireEvent.click(await screen.findByRole('button', { name: 'Коллизия 1' }));

    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText('Переносим трубу')).toBeTruthy();
    expect(within(drawer).getByRole('link', { name: 'Открыть в 3D' }).getAttribute('href')).toBe('/clash/checks/p1?collision=c1');
    expect(screen.queryByText('картинка ещё рисуется')).toBeNull();
  });
});
