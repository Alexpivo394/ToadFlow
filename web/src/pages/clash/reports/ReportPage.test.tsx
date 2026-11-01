import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { navigateTo, renderApp } from '../../../test/renderApp';
import { designer, pairUrl, report, responses, row } from './reportFixtures';

vi.mock('../../../lib/clipboard', () => ({ copyText: vi.fn(async () => true) }));
vi.mock('../../../api/client', async (original) => ({ ...(await original<typeof import('../../../api/client')>()), downloadFile: vi.fn(async () => {}) }));

const { copyText } = await import('../../../lib/clipboard');
const { downloadFile } = await import('../../../api/client');

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const rowCalls = () => calls().filter(([url]) => url.startsWith('/api/clash/reports/r2/collisions')).map(([url]) => new URL(url, 'http://x').searchParams);
const tree = () => screen.getByTestId('report-tree');

describe('Report page', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('Tree_DisciplinesInServerOrder_PairsUnderEach', async () => {
    renderApp('/clash/reports/r2', responses());

    await screen.findByRole('heading', { name: /Отчёт от 31\.10\.2026/ });
    expect(within(tree()).getAllByTestId('tree-discipline').map((d) => d.getAttribute('data-name'))).toEqual(['АР', 'ОВ', 'ВК']);
    expect(within(tree()).getAllByRole('button', { name: 'Пара АР × ОВ' })).toHaveLength(2);
    expect(within(tree()).getAllByRole('button', { name: 'Пара ОВ × ВК' })).toHaveLength(2);
  });

  it('NoSelection_Coordinator_ShowsSummary', async () => {
    renderApp('/clash/reports/r2', responses());

    expect(await screen.findByRole('heading', { name: /Отчёт от 31\.10\.2026/ })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Корпус 1' }).getAttribute('href')).toBe('/clash/checks/p1');
    expect(screen.getByText(/вручную \(Иванов\)/)).toBeTruthy();
    const tiles = screen.getByTestId('report-tiles');
    expect(within(tiles).getByText('Новых').parentElement!.textContent).toContain('12');
    expect(within(tiles).getByText('Фиктивных').parentElement!.textContent).toContain('5');
    const summaryPairs = screen.getByTestId('report-pairs');
    expect(within(summaryPairs).getAllByRole('button').map((b) => b.textContent)).toEqual(['АР × ОВ', 'АР × ВК', 'ОВ × ВК']);
    expect(rowCalls()).toHaveLength(0);
  });

  it('NoSelection_Designer_OpensOwnDiscipline', async () => {
    renderApp('/clash/reports/r2', responses(designer));

    await waitFor(() => expect(rowCalls().some((p) => p.get('discipline') === 'ОВ')).toBe(true));
    expect(screen.queryByTestId('report-tiles')).toBeNull();
    expect(rowCalls().every((p) => p.get('pageSize') === '100')).toBe(true);
  });

  it('Designer_NoOwnDisciplineInReport_ShowsSummary', async () => {
    renderApp('/clash/reports/r2', responses({ '/api/auth/me': { ...designer['/api/auth/me'], disciplines: ['ЭОМ'] } }));

    expect(await screen.findByTestId('report-tiles')).toBeTruthy();
    expect(rowCalls()).toHaveLength(0);
  });

  it('Designer_SummaryClick_StaysOnSummary', async () => {
    renderApp('/clash/reports/r2', responses(designer));
    await waitFor(() => expect(rowCalls().length).toBeGreaterThan(0));

    fireEvent.click(within(tree()).getByRole('button', { name: 'Сводка' }));

    expect(await screen.findByTestId('report-tiles')).toBeTruthy();
  });

  it('MineToggle_HidesOtherDisciplines', async () => {
    renderApp('/clash/reports/r2', responses(designer));
    await waitFor(() => expect(rowCalls().length).toBeGreaterThan(0));
    expect(within(tree()).getAllByTestId('tree-discipline')).toHaveLength(1);

    fireEvent.click(within(tree()).getByText('Все'));

    await waitFor(() => expect(within(tree()).getAllByTestId('tree-discipline')).toHaveLength(3));
    expect(localStorage.getItem('clash.report.mine')).toBe('0');
  });

  it('Rows_LevelHeadersWithTotals', async () => {
    renderApp(pairUrl('АР × ОВ'), responses({
      '/api/clash/reports/r2/collisions': { total: 3, items: [row(1), row(2), row(3, '')] },
      '/api/clash/reports/r2/levels': [{ pair: 'АР × ОВ', level: 'Этаж 1', count: 2 }, { pair: 'АР × ОВ', level: '', count: 1 }],
    }));

    await screen.findByText('№ 3');
    await waitFor(() => expect(screen.getAllByTestId('level-header').map((h) => h.textContent)).toEqual(['Этаж 1 · 2', 'Без уровня · 1']));
    expect(rowCalls()[0].get('pair')).toBe('АР × ОВ');

    fireEvent.click(screen.getByText('По глубине'));

    await waitFor(() => expect(rowCalls().some((p) => p.get('sort') === 'depth')).toBe(true));
    await waitFor(() => expect(screen.queryAllByTestId('level-header')).toHaveLength(0));
  });

  it('Rows_LevelContinuesFromPreviousPage_HeaderMarked', async () => {
    renderApp(pairUrl('АР × ОВ') + '&page=2', responses({
      '/api/clash/reports/r2/collisions': { total: 150, items: [row(101), row(102)] },
      '/api/clash/reports/r2/levels': [{ pair: 'АР × ОВ', level: 'Этаж 1', count: 150 }],
    }));

    await screen.findByText('№ 101');
    expect(rowCalls()[0].get('page')).toBe('2');
    await waitFor(() => expect(screen.getAllByTestId('level-header')[0].textContent).toBe('Этаж 1 · 150 (продолжение)'));
  });

  it('Discipline_RowsGroupedByPairThenLevel', async () => {
    renderApp('/clash/reports/r2?discipline=' + encodeURIComponent('ОВ'), responses({
      '/api/clash/reports/r2/collisions': {
        total: 3, items: [row(1), row(2, 'Этаж 2', { levelA: 'Этаж 2', levelB: 'Этаж 2' }), row(3, 'Этаж 1', { disciplineA: 'ВК' })],
      },
      '/api/clash/reports/r2/levels': [
        { pair: 'АР × ОВ', level: 'Этаж 1', count: 1 }, { pair: 'АР × ОВ', level: 'Этаж 2', count: 1 }, { pair: 'ВК × ОВ', level: 'Этаж 1', count: 1 },
      ],
    }));

    await screen.findByText('№ 3');
    await waitFor(() => expect(screen.getAllByTestId(/^(pair|level)-header$/).map((h) => h.textContent))
      .toEqual(['АР × ОВ', 'Этаж 1 · 1', 'Этаж 2 · 1', 'ОВ × ВК', 'Этаж 1 · 1']));
  });

  it('Summary_PairClick_SelectsPair', async () => {
    renderApp('/clash/reports/r2', responses());

    fireEvent.click(within(await screen.findByTestId('report-pairs')).getByRole('button', { name: 'ОВ × ВК' }));

    await waitFor(() => expect(rowCalls().some((p) => p.get('pair') === 'ВК × ОВ')).toBe(true));
  });

  it('Filters_LevelSelectFromLevels_DisciplineAndPairNotInRow', async () => {
    const all = [{ pair: 'АР × ОВ', level: 'Этаж 1', count: 2 }, { pair: 'АР × ОВ', level: '', count: 1 }];
    renderApp(pairUrl('АР × ОВ'), responses({
      '/api/clash/reports/r2/collisions': (url: string) =>
        (new URL(url, 'http://x').searchParams.get('level') === '-' ? { total: 1, items: [row(3, '')] } : { total: 2, items: [row(1), row(2)] }),
      '/api/clash/reports/r2/levels': (url: string) => (new URL(url, 'http://x').searchParams.get('level') === '-' ? [all[1]] : all),
    }));
    await screen.findByText('№ 1');
    expect(screen.queryByPlaceholderText('Раздел')).toBeNull();
    expect(screen.queryByPlaceholderText('Пара разделов')).toBeNull();

    fireEvent.click(screen.getByPlaceholderText('Уровень'));
    fireEvent.click(await screen.findByRole('option', { name: 'Без уровня · 1' }));

    await waitFor(() => expect(rowCalls().some((p) => p.get('level') === '-')).toBe(true));
    await screen.findByText('№ 3');
    await waitFor(() => expect(screen.getAllByTestId('level-header').map((h) => h.textContent)).toEqual(['Без уровня · 1']));
    fireEvent.click(screen.getByPlaceholderText('Уровень'));
    expect(await screen.findByRole('option', { name: 'Этаж 1 · 2' })).toBeTruthy();
  });

  it('ReportPage_OldRun_ShowsNewerBanner', async () => {
    renderApp('/clash/reports/r2', responses({ '/api/clash/reports/r2': report({ newerRunId: 'r3', newerRunAt: '2026-11-01T09:00:00Z' }) }));

    expect(await screen.findByText(/Есть более новый прогон от 01\.11\.2026/)).toBeTruthy();
    expect(screen.getByRole('link', { name: /Открыть новый отчёт/ }).getAttribute('href')).toBe('/clash/reports/r3?scope=changes');
  });

  it('ReportPage_NewerReportLink_KeepsFilters', async () => {
    renderApp(pairUrl('АР × ОВ') + '&level=' + encodeURIComponent('Этаж 1') + '&q=17', responses({
      '/api/clash/reports/r2': report({ newerRunId: 'r3', newerRunAt: '2026-11-01T09:00:00Z' }),
      '/api/clash/reports/r3': report({ run: { ...report().run, runId: 'r3' } }),
      '/api/clash/reports/r3/collisions': { total: 0, items: [] },
      '/api/clash/reports/r3/levels': [{ pair: 'АР × ОВ', level: 'Этаж 1', count: 2 }],
    }));

    const link = await screen.findByRole('link', { name: /Открыть новый отчёт/ });
    const [path, query] = link.getAttribute('href')!.split('?');
    expect(path).toBe('/clash/reports/r3');
    expect(new URLSearchParams(query).get('level')).toBe('Этаж 1');
    expect(new URLSearchParams(query).get('q')).toBe('17');
    expect(new URLSearchParams(query).get('pair')).toBe('АР × ОВ');
    fireEvent.click(link);

    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/clash/reports/r3/collisions'))).toBe(true));
    await waitFor(() => expect((screen.getByPlaceholderText('Уровень') as HTMLInputElement).value).toBe('Этаж 1 · 2'));
    expect((screen.getByPlaceholderText('№, ID или UniqueId') as HTMLInputElement).value).toBe('17');
  });

  it('ReportPage_NavigateWithoutParams_ClearsSearchField', async () => {
    renderApp(pairUrl('АР × ОВ') + '&q=17', responses({
      '/api/clash/reports/r3': report({ run: { ...report().run, runId: 'r3' } }),
      '/api/clash/reports/r3/collisions': { total: 0, items: [] },
      '/api/clash/reports/r3/levels': [],
    }));
    expect(((await screen.findByPlaceholderText('№, ID или UniqueId')) as HTMLInputElement).value).toBe('17');

    navigateTo(pairUrl('АР × ОВ').replace('r2', 'r3'));

    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/clash/reports/r3/collisions'))).toBe(true));
    await waitFor(() => expect((screen.getByPlaceholderText('№, ID или UniqueId') as HTMLInputElement).value).toBe(''));
  });

  it('ReportPage_SearchEnter_AppliesAndKeepsFocus', async () => {
    renderApp(pairUrl('АР × ОВ'), responses());
    const q = (await screen.findByPlaceholderText('№, ID или UniqueId')) as HTMLInputElement;
    q.focus();
    fireEvent.change(q, { target: { value: '17' } });

    fireEvent.keyDown(q, { key: 'Enter' });

    await waitFor(() => expect(rowCalls().some((p) => p.get('q') === '17')).toBe(true));
    expect(document.activeElement).toBe(screen.getByPlaceholderText('№, ID или UniqueId'));
  });

  it('ReportPage_ScopeSwitch_RequestsOpen', async () => {
    renderApp(pairUrl('АР × ОВ'), responses());

    fireEvent.click(await screen.findByText('Открытые сейчас'));

    await waitFor(() => expect(rowCalls().some((p) => p.get('scope') === 'open')).toBe(true));
  });

  it('ReportPage_Excel_DownloadsWithFiltersAndBaseUrl', async () => {
    renderApp(pairUrl('АР × ОВ'), responses());

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
    await waitFor(() => expect(rowCalls().length).toBeGreaterThan(0));
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
    renderApp(pairUrl('АР × ОВ'), responses());

    fireEvent.click(await screen.findByRole('button', { name: 'Коллизия 1' }));

    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText('Переносим трубу')).toBeTruthy();
    expect(within(drawer).getByRole('link', { name: 'Открыть в 3D' }).getAttribute('href')).toBe('/clash/checks/p1?collision=c1');
    expect(screen.queryByText('картинка ещё рисуется')).toBeNull();
  });

  it('Row_CompactWithModelNameAndNote', async () => {
    renderApp(pairUrl('АР × ОВ'), responses());

    const first = await screen.findByRole('button', { name: 'Коллизия 1' });
    expect(first.textContent).toContain('POL_K1_AR.rvt');
    expect(first.textContent).toContain('Переносим трубу');
    expect(first.textContent).toContain('вернулась');
  });
});
