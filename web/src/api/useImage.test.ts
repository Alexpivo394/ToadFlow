import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setToken, setUnauthorizedHandler } from './client';
import { useImage } from './useImage';

describe('useImage', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('useImage_FetchesWithTokenAndRevokes', async () => {
    setToken('t-1');
    const fetchMock = vi.fn(async () => new Response(new Blob([new Uint8Array([137, 80])], { type: 'image/png' }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const create = vi.fn(() => 'blob:one');
    const revoke = vi.fn();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke }));

    const { result, unmount, rerender } = renderHook(({ path }) => useImage(path), { initialProps: { path: '/api/clash/collisions/c1/image?v=ab' as string | null } });

    expect(result.current.isLoading).toBe(true);
    await waitFor(() => expect(result.current.url).toBe('blob:one'));
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/clash/collisions/c1/image?v=ab');
    expect(new Headers(init.headers).get('Authorization')).toBe('Bearer t-1');
    expect(result.current.error).toBeNull();

    rerender({ path: null });
    expect(revoke).toHaveBeenCalledWith('blob:one');
    expect(result.current.url).toBeNull();
    unmount();
  });

  it('useImage_401_SignsOut', async () => {
    const spy = vi.fn();
    setUnauthorizedHandler(spy);
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 401 })));

    const { result } = renderHook(() => useImage('/api/clash/collisions/c3/image?v=1'));

    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    expect(result.current.error).not.toBe('нет картинки');
    setUnauthorizedHandler(() => {});
  });

  it('useImage_410_ServerReason', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'Слепок удалён' }), { status: 410 })));

    const { result } = renderHook(() => useImage('/api/clash/collisions/c2/image?v=1'));

    await waitFor(() => expect(result.current.error).toBe('Слепок удалён'));
    expect(result.current.url).toBeNull();
    expect(result.current.isLoading).toBe(false);
  });

  it('useImage_NoJson_DefaultReason', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('x', { status: 500 })));

    const { result } = renderHook(() => useImage('/api/clash/collisions/c4/image?v=1'));

    await waitFor(() => expect(result.current.error).toBe('нет картинки'));
  });
});
