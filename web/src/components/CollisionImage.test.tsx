import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CollisionImage } from './CollisionImage';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

/** IntersectionObserver, которым тест сам говорит, видна ли картинка. */
function stubObserver() {
  const observers: { callback: IntersectionObserverCallback; options?: IntersectionObserverInit }[] = [];
  class Observer {
    constructor(callback: IntersectionObserverCallback, options?: IntersectionObserverInit) {
      observers.push({ callback, options });
    }
    observe() {}
    disconnect() {}
    unobserve() {}
    takeRecords() {
      return [];
    }
  }
  vi.stubGlobal('IntersectionObserver', Observer);
  return {
    observers,
    show: () => act(() => observers.forEach((o) => o.callback([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver))),
  };
}

const png = () => new Response(new Blob([new Uint8Array([137, 80])]), { status: 200 });

describe('CollisionImage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('CollisionImage_404_ShowsServerReason', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'Элемент A 123 не найден в слепках прогона' }), { status: 404 })));

    render(<MantineProvider><CollisionImage collisionId="c1" imageVersion="v1" width={160} /></MantineProvider>);

    expect(await screen.findByText('Элемент A 123 не найден в слепках прогона', {}, { timeout: 5000 })).toBeTruthy();
    expect(screen.queryByText('картинка ещё рисуется')).toBeNull();
  });

  it('Image_NotInView_NotFetched', async () => {
    const observer = stubObserver();
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:x', revokeObjectURL: () => {} }));
    vi.stubGlobal('fetch', vi.fn(async () => png()));

    render(<MantineProvider><CollisionImage collisionId="c1" imageVersion="v1" /></MantineProvider>);
    await act(async () => {});

    expect(calls()).toHaveLength(0);
    expect(observer.observers[0].options?.rootMargin).toBe('200px');
    observer.show();
    expect(await screen.findByAltText('Картинка коллизии')).toBeTruthy();
    expect(calls()).toHaveLength(1);
  });

  it('Image_Unmount_RequestAborted', async () => {
    let signal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => {
      signal = init?.signal ?? undefined;
      return new Promise<Response>(() => {});
    }));

    const view = render(<MantineProvider><CollisionImage collisionId="c1" imageVersion="v1" /></MantineProvider>);
    await act(async () => {});
    expect(signal?.aborted).toBe(false);

    view.unmount();

    expect(signal?.aborted).toBe(true);
  });

  it('Image_429_RetriesAfterHeader', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:x', revokeObjectURL: () => {} }));
    const busy = () => new Response(JSON.stringify({ error: 'Картинок слишком много в очереди' }), { status: 429, headers: { 'Retry-After': '2' } });
    const responses = [busy(), png()];
    vi.stubGlobal('fetch', vi.fn(async () => responses.shift()!));

    render(<MantineProvider><CollisionImage collisionId="c1" imageVersion="v1" /></MantineProvider>);
    await act(async () => {});
    expect(calls()).toHaveLength(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1500);
    });
    expect(calls()).toHaveLength(1); // ждёт, сколько сказал сервер
    await act(async () => {
      await vi.advanceTimersByTimeAsync(600);
    });

    expect(calls()).toHaveLength(2);
    expect(await screen.findByAltText('Картинка коллизии')).toBeTruthy();
  });
});
