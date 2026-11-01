import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../../test/renderApp';
import { pairUrl, responses, row } from './reportFixtures';

vi.mock('../../../api/client', async (original) => ({ ...(await original<typeof import('../../../api/client')>()), downloadFile: vi.fn(async () => {}) }));

const { downloadFile } = await import('../../../api/client');

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

const archive = (extra: object = {}) => ({
  id: 'a1', runId: 'r2', discipline: null, state: 'running', pairsDone: 1, pairsTotal: 3, queuePosition: null, sizeBytes: null,
  expiresAt: null, error: null, ...extra,
});

describe('Report export', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('Pair_HtmlButton_DownloadsFileWithPairLabelAndCount', async () => {
    renderApp(pairUrl('ВК × ОВ') + '&status=New', responses({
      '/api/clash/reports/r2/collisions': { total: 30, items: [row(1)] },
    }));

    fireEvent.click(await screen.findByRole('button', { name: 'HTML: ОВ × ВК (30)' }));

    await waitFor(() => expect(downloadFile).toHaveBeenCalled());
    const [path, name] = vi.mocked(downloadFile).mock.calls[0];
    const url = new URL(path, 'http://x');
    expect(url.pathname).toBe('/api/clash/reports/r2/export.html');
    expect(url.searchParams.get('pair')).toBe('ВК × ОВ');
    expect(url.searchParams.get('status')).toBe('New');
    expect(url.searchParams.get('baseUrl')).toBe(window.location.origin);
    expect(name).toContain('ОВ × ВК');
    expect(name).toMatch(/\.html$/);
  });

  it('Discipline_HtmlButton_StartsArchive', async () => {
    renderApp('/clash/reports/r2?discipline=' + encodeURIComponent('ОВ'), responses({
      'POST /api/clash/reports/r2/archive': archive({ discipline: 'ОВ', state: 'queued', pairsDone: 0, pairsTotal: 2, queuePosition: 1 }),
    }));

    fireEvent.click(await screen.findByRole('button', { name: 'HTML: раздел ОВ — архив (2 пары)' }));

    await waitFor(() => expect(calls().some(([, init]) => init?.method === 'POST')).toBe(true));
    const [path] = calls().find(([, init]) => init?.method === 'POST')!;
    const url = new URL(path, 'http://x');
    expect(url.pathname).toBe('/api/clash/reports/r2/archive');
    expect(url.searchParams.get('discipline')).toBe('ОВ');
    expect(url.searchParams.get('scope')).toBe('changes');
    expect(url.searchParams.get('baseUrl')).toBe(window.location.origin);
    expect(await screen.findByText(/в очереди/)).toBeTruthy();
  });

  it('Summary_HtmlButton_ArchiveAll', async () => {
    renderApp('/clash/reports/r2', responses());

    expect(await screen.findByRole('button', { name: 'HTML: всё — архив (3 пары)' })).toBeTruthy();
  });

  it('Archive_Running_ShowsProgressAndCancel', async () => {
    let current: object | null = archive();
    renderApp('/clash/reports/r2', responses({
      '/api/clash/report-archives/current': () => current,
      'DELETE /api/clash/report-archives/a1': () => {
        current = null;
        return null;
      },
    }));

    expect(await screen.findByText('Готовим архив: 1 из 3 пар')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отменить' }));

    await waitFor(() => expect(calls().some(([url, init]) => init?.method === 'DELETE' && url === '/api/clash/report-archives/a1')).toBe(true));
    await waitFor(() => expect(screen.queryByText(/Готовим архив/)).toBeNull());
  });

  it('Archive_Ready_DownloadButtonWithSize', async () => {
    renderApp('/clash/reports/r2', responses({
      '/api/clash/report-archives/current': archive({ state: 'ready', pairsDone: 3, sizeBytes: 812 * 1024 * 1024, expiresAt: '2026-11-02T09:00:00Z' }),
    }));

    fireEvent.click(await screen.findByRole('button', { name: 'Скачать архив (812 МБ)' }));

    await waitFor(() => expect(downloadFile).toHaveBeenCalled());
    const [path, name] = vi.mocked(downloadFile).mock.calls[0];
    expect(path).toBe('/api/clash/report-archives/a1/download');
    expect(name).toMatch(/\.zip$/);
  });

  it('Archive_Queued_ShowsPosition', async () => {
    renderApp('/clash/reports/r2', responses({
      '/api/clash/report-archives/current': archive({ state: 'queued', pairsDone: 0, queuePosition: 2 }),
    }));

    expect(await screen.findByText('Архив в очереди: перед ним 2')).toBeTruthy();
  });

  it('Archive_FailedOrExpired_Message', async () => {
    const view = renderApp('/clash/reports/r2', responses({
      '/api/clash/report-archives/current': archive({ state: 'failed', error: 'диск полон' }),
    }));
    expect(await screen.findByText('Архив не собрался: диск полон')).toBeTruthy();
    view.unmount();

    renderApp('/clash/reports/r2', responses({ '/api/clash/report-archives/current': archive({ state: 'expired' }) }));
    expect(await screen.findByText('Архив устарел — выгрузите заново')).toBeTruthy();
  });

  it('Archive_OtherRun_Hidden', async () => {
    renderApp('/clash/reports/r2', responses({
      '/api/clash/report-archives/current': archive({ runId: 'r1' }),
    }));

    await screen.findByRole('button', { name: 'HTML: всё — архив (3 пары)' });
    await waitFor(() => expect(calls().some(([url]) => url === '/api/clash/report-archives/current')).toBe(true));
    expect(screen.queryByText(/Готовим архив/)).toBeNull();
  });

  it('Excel_UsesSameSelection', async () => {
    renderApp('/clash/reports/r2?discipline=' + encodeURIComponent('ОВ') + '&sort=depth', responses());

    fireEvent.click(await screen.findByRole('button', { name: 'Excel' }));

    await waitFor(() => expect(downloadFile).toHaveBeenCalled());
    const url = new URL(vi.mocked(downloadFile).mock.calls[0][0], 'http://x');
    expect(url.pathname).toBe('/api/clash/reports/r2/export.xlsx');
    expect(url.searchParams.get('discipline')).toBe('ОВ');
    expect(url.searchParams.get('sort')).toBe('depth');
  });
});
