import { afterEach, describe, expect, it, vi } from 'vitest';
import { downloadFile, setUnauthorizedHandler } from './client';

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  setUnauthorizedHandler(() => {});
});

describe('Download', () => {
  it('Download_401_SignsOut_RevokeDeferred', async () => {
    const signOut = vi.fn();
    setUnauthorizedHandler(signOut);
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })));

    await expect(downloadFile('/api/clash/reports/r1/export.xlsx', 'r1.xlsx')).rejects.toMatchObject({ status: 401 });
    expect(signOut).toHaveBeenCalledTimes(1);

    vi.useFakeTimers();
    const revoke = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:x', revokeObjectURL: revoke }));
    vi.stubGlobal('fetch', vi.fn(async () => new Response('data', { status: 200 })));
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    await downloadFile('/api/clash/reports/r1/export.xlsx', 'r1.xlsx');

    expect(click).toHaveBeenCalled();
    expect(revoke).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1000);
    expect(revoke).toHaveBeenCalledWith('blob:x');
    click.mockRestore();
  });
});
