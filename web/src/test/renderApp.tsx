import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { MantineProvider } from '@mantine/core';
import { DatesProvider } from '@mantine/dates';
import { ModalsProvider } from '@mantine/modals';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi } from 'vitest';
import { App } from '../App';
import { setToken } from '../api/client';
import { AuthProvider } from '../auth/AuthContext';
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

/** Ответы сервера по пути (без query); /api/auth/me — вошедший администратор. */
export function renderApp(path: string, responses: Record<string, unknown>) {
  const all: Record<string, unknown> = {
    '/api/auth/me': { id: 'u1', login: 'admin', displayName: 'Администратор', role: 'Admin', isDisabled: false, lastLoginAt: null },
    ...responses,
  };
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string) => {
      const key = input.split('?')[0];
      const body = all[key];
      return new Response(body === undefined ? JSON.stringify({ error: 'нет в тесте: ' + key }) : JSON.stringify(body), {
        status: body === undefined ? 404 : 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
  setToken('test-token');

  return render(
    <MantineProvider theme={theme}>
      <DatesProvider settings={{ locale: 'ru' }}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ModalsProvider>
            <MemoryRouter initialEntries={[path]}>
              <AuthProvider>
                <App />
              </AuthProvider>
            </MemoryRouter>
          </ModalsProvider>
        </QueryClientProvider>
      </DatesProvider>
    </MantineProvider>,
  );
}
