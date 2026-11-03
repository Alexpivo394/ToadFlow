import { act, fireEvent, render, waitFor, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { createMemoryRouter, RouterProvider, useNavigate } from 'react-router';
import { MantineProvider } from '@mantine/core';
import { DatesProvider } from '@mantine/dates';
import { ModalsProvider } from '@mantine/modals';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi } from 'vitest';
import { appRoutes } from '../App';
import { setToken } from '../api/client';
import { theme } from '../theme';

vi.mock('@microsoft/signalr', () => {
  class Builder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return { on: () => {}, start: () => Promise.resolve(), stop: () => Promise.resolve() };
    }
  }
  return { HubConnectionBuilder: Builder, LogLevel: { Warning: 3 } };
});

let navigateFn: ((to: string | number) => void) | null = null;

function NavigateProbe() {
  const navigate = useNavigate();
  navigateFn = (to) => { void (typeof to === 'number' ? navigate(to) : navigate(to)); };
  return null;
}

/** Переход внутри отрисованного приложения (как по ссылке): для проверки, что страница следует за адресом. */
export function navigateTo(to: string) {
  act(() => navigateFn?.(to));
}

/** Кнопка браузера «Назад». */
export function goBack() {
  act(() => navigateFn?.(-1));
}

/** Ответ сервера с ошибкой: статус и текст в поле <c>error</c>, как у API. */
export class Failure {
  constructor(
    public readonly status: number,
    public readonly error: string,
    /** Остальные поля тела ошибки — например, конфликты сохранения. */
    public readonly extra: object = {},
  ) {}
}

/**
 * Ответы сервера по пути (без query) или по «МЕТОД путь» — он важнее; `null` — ответ 204; `ArrayBuffer` — двоичный ответ;
 * `Failure` — ошибка; функция — ответ по адресу запроса с параметрами;
 * /api/auth/me — вошедший администратор. <c>history</c> — адреса, открытые до <c>path</c> (для «Назад»).
 */
export function renderApp(path: string, responses: Record<string, unknown>, history: string[] = []) {
  stubFetch(responses);
  const router = createMemoryRouter(appRoutes(<NavigateProbe />), { initialEntries: [...history, path] });

  return render(
    <MantineProvider theme={theme}>
      <DatesProvider settings={{ locale: 'ru' }}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ModalsProvider>
            <RouterProvider router={router} />
          </ModalsProvider>
        </QueryClientProvider>
      </DatesProvider>
    </MantineProvider>,
  );
}

/** Отдельный компонент в обвязке приложения (тема, запросы, адрес) с теми же ответами сервера, что у <c>renderApp</c>. */
export function renderUi(ui: ReactNode, responses: Record<string, unknown>, path = '/') {
  stubFetch(responses);
  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ModalsProvider>
          <RouterProvider router={createMemoryRouter([{ path: '*', element: ui }], { initialEntries: [path] })} />
        </ModalsProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function stubFetch(responses: Record<string, unknown>) {
  const all: Record<string, unknown> = {
    '/api/auth/me': { id: 'u1', login: 'admin', displayName: 'Администратор', role: 'Admin', isDisabled: false, lastLoginAt: null },
    ...responses,
  };
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string, init?: RequestInit) => {
      const key = input.split('?')[0];
      const method = `${init?.method ?? 'GET'} ${key}`;
      const found = method in all ? all[method] : all[key];
      const body = typeof found === 'function' ? (found as (url: string, init?: RequestInit) => unknown)(input, init) : found;
      if (body === null) return new Response(null, { status: 204 });
      if (body instanceof ArrayBuffer) return new Response(body, { status: 200, headers: { 'Content-Type': 'application/octet-stream' } });
      if (body instanceof Failure)
        return new Response(JSON.stringify({ error: body.error, ...body.extra }), { status: body.status, headers: { 'Content-Type': 'application/json' } });
      return new Response(body === undefined ? JSON.stringify({ error: 'нет в тесте: ' + key }) : JSON.stringify(body), {
        status: body === undefined ? 404 : 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
  setToken('test-token');
}

/** Выбор пункта в Mantine Select: открыть список поля и нажать пункт именно этого списка (у соседних полей те же пункты). */
export async function pickOption(input: HTMLElement, name: string | RegExp) {
  fireEvent.click(input);
  const list = await waitFor(() => {
    const element = document.getElementById(input.getAttribute('aria-controls') ?? '');
    if (!element) throw new Error('Список поля не открылся');
    return element;
  });
  fireEvent.click(within(list).getByRole('option', { name, hidden: true }));
}
