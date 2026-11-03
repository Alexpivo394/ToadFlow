import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { useQueryClient } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { apiFetch, getToken, setSession, setToken } from '../api/client';
import { Failure, renderApp, renderUi } from '../test/renderApp';
import { AuthProvider, useAuth } from './AuthContext';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const posts = (path: string) => (fetch as unknown as FetchMock).mock.calls.filter(([url, init]) => url === path && init?.method === 'POST');
const bearer = (init?: RequestInit) => new Headers(init?.headers).get('Authorization');

const me = { id: 'u1', login: 'ivanov', displayName: 'Иванов', role: 'Operator', isDisabled: false, lastLoginAt: null, disciplines: [] };
const anonymous = { '/api/auth/me': new Failure(401, 'Нужен вход') };

function clearSession() {
  setToken(null);
  localStorage.clear();
  sessionStorage.clear();
}

async function signIn(remember: boolean) {
  fireEvent.change(await screen.findByLabelText(/Логин/), { target: { value: 'ivanov' } });
  fireEvent.change(screen.getByLabelText(/Пароль/), { target: { value: 'secret-123' } });
  const box = screen.getByLabelText('Оставаться в системе') as HTMLInputElement;
  if (box.checked !== remember) fireEvent.click(box);
  fireEvent.click(screen.getByRole('button', { name: 'Войти' }));
}

describe('Sign in and session', () => {
  beforeEach(clearSession);
  afterEach(clearSession);

  it('Login_RememberChecked_StoresRefreshInLocalStorage', async () => {
    renderApp('/', { ...anonymous, 'POST /api/auth/login': { token: 't1', refreshToken: 'r1', user: me } });

    await signIn(true);

    await waitFor(() => expect(localStorage.getItem('toadflow.refresh')).toBe('r1'));
    expect(localStorage.getItem('toadflow.token')).toBe('t1');
    expect(sessionStorage.getItem('toadflow.token')).toBeNull();
    expect(JSON.parse(String(posts('/api/auth/login')[0][1]!.body))).toMatchObject({ remember: true });
  });

  it('Login_RememberUnchecked_SessionStorageOnly', async () => {
    renderApp('/', { ...anonymous, 'POST /api/auth/login': { token: 't1', refreshToken: null, user: me } });

    await signIn(false);

    await waitFor(() => expect(sessionStorage.getItem('toadflow.token')).toBe('t1'));
    expect(localStorage.getItem('toadflow.token')).toBeNull();
    expect(localStorage.getItem('toadflow.refresh')).toBeNull();
  });

  /** Токен истёк: параллельные запросы получили 401 — сессия обновляется одним запросом, и оба повторяются с новым токеном. */
  it('Api401_RefreshOnceThenRetry', async () => {
    renderUi(<div />, {
      '/api/things': (_url: string, init?: RequestInit) => (bearer(init) === 'Bearer fresh' ? [1, 2] : new Failure(401, 'истёк')),
      'POST /api/auth/refresh': { token: 'fresh', refreshToken: 'r2', user: me },
    });
    setSession('stale', 'r1');

    const [a, b] = await Promise.all([apiFetch<number[]>('/api/things'), apiFetch<number[]>('/api/things')]);

    expect(a).toEqual([1, 2]);
    expect(b).toEqual([1, 2]);
    expect(posts('/api/auth/refresh')).toHaveLength(1);
    expect(JSON.parse(String(posts('/api/auth/refresh')[0][1]!.body))).toEqual({ refreshToken: 'r1' });
    expect(localStorage.getItem('toadflow.refresh')).toBe('r2');
  });

  it('Logout_ClearsQueryCache', async () => {
    let cached: unknown = 'не прочитано';
    function Probe() {
      const queryClient = useQueryClient();
      const { user, logout } = useAuth();
      return (
        <div>
          <span>{user?.login ?? 'никого'}</span>
          <button onClick={() => queryClient.setQueryData(['my-collisions'], ['чужие данные'])}>запомнить</button>
          <button onClick={logout}>выйти</button>
          <button onClick={() => (cached = queryClient.getQueryData(['my-collisions']))}>прочитать</button>
        </div>
      );
    }
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': me, 'POST /api/auth/logout': null });

    expect(await screen.findByText('ivanov')).toBeTruthy();
    fireEvent.click(screen.getByText('запомнить'));
    fireEvent.click(screen.getByText('выйти'));
    fireEvent.click(screen.getByText('прочитать'));

    expect(cached).toBeUndefined();
    expect(screen.getByText('никого')).toBeTruthy();
    expect(getToken()).toBeNull();
  });

  /** F5, пока сервер перезапускается: вход не выбрасывается, страница ждёт сервер. */
  it('Auth_MeFails503_KeepsToken', async () => {
    renderApp('/', { '/api/auth/me': new Failure(503, 'Сервер перезапускается') });

    expect(await screen.findByText('Сервер недоступен')).toBeTruthy();
    expect(getToken()).toBe('test-token');
    expect(screen.queryByRole('button', { name: 'Войти' })).toBeNull();
    await act(async () => {}); // повтор по таймеру не сбрасывает вход
    expect(getToken()).toBe('test-token');
  });

  it('Login_Pending_InfoNotError', async () => {
    renderApp('/', { ...anonymous, 'POST /api/auth/login': new Failure(403, 'Заявка ждёт подтверждения координатора') });

    await signIn(true);

    const alert = await screen.findByText('Заявка ждёт подтверждения координатора');
    expect(alert.closest('[role="alert"]')?.getAttribute('style') ?? '').toContain('blue');
  });
});
