import type { ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './client';
import { retryPolicy, useApi } from './queries';

afterEach(() => vi.unstubAllGlobals());

describe('Queries', () => {
  it('Query_4xx_NotRetried', () => {
    expect(retryPolicy(0, new ApiError(404, 'Не найдено'))).toBe(false);
    expect(retryPolicy(0, new ApiError(403, 'Недостаточно прав'))).toBe(false);
    expect(retryPolicy(0, new ApiError(502, 'Ошибка сервера (502)'))).toBe(true);
    expect(retryPolicy(0, new TypeError('Failed to fetch'))).toBe(true);
    expect(retryPolicy(1, new ApiError(500, 'Ошибка сервера (500)'))).toBe(false);
  });

  it('Queries_PassAbortSignal', async () => {
    const fetchMock = vi.fn(async () => new Response('{"ok":true}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;

    const { result } = renderHook(() => useApi<{ ok: boolean }>(['probe'], '/api/probe'), { wrapper });

    await waitFor(() => expect(result.current.data).toEqual({ ok: true }));
    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(init.signal).toBeInstanceOf(AbortSignal);
  });
});
