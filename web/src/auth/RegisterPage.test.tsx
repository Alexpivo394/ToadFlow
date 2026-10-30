import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Failure, renderApp } from '../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const posts = (path: string) => calls().filter(([url, init]) => url === path && init?.method === 'POST');

/** Никто не вошёл: /api/auth/me отвечает 401. */
const anonymous = { '/api/auth/me': new Failure(401, 'Нужен вход') };

describe('Registration', () => {
  it('Register_SubmitsDisciplines_ShowsWaitMessage', async () => {
    renderApp('/register', { ...anonymous, '/api/auth/disciplines': ['АР', 'ВК', 'ОВ'], 'POST /api/auth/register': null });

    fireEvent.change(await screen.findByLabelText(/Имя и фамилия/), { target: { value: 'Ольга Ковалёва' } });
    fireEvent.change(screen.getByLabelText(/Логин/), { target: { value: 'kovaleva' } });
    fireEvent.change(screen.getByLabelText(/Пароль/), { target: { value: 'secret-123' } });
    fireEvent.change(screen.getByLabelText('Почта'), { target: { value: 'olga@example.com' } });
    fireEvent.click(screen.getAllByLabelText('Разделы').find((e) => (e as HTMLInputElement).type !== 'hidden')!);
    fireEvent.click(await screen.findByRole('option', { name: 'ОВ' }));
    fireEvent.click(screen.getByRole('button', { name: 'Отправить заявку' }));

    expect(await screen.findByText(/Заявка отправлена/)).toBeTruthy();
    const body = JSON.parse(String(posts('/api/auth/register')[0][1]!.body));
    expect(body).toMatchObject({ login: 'kovaleva', displayName: 'Ольга Ковалёва', password: 'secret-123', disciplines: ['ОВ'], email: 'olga@example.com' });
  });

  it('Login_Pending_ShowsReason', async () => {
    renderApp('/', { ...anonymous, 'POST /api/auth/login': new Failure(403, 'Заявка ждёт подтверждения координатора') });

    fireEvent.change(await screen.findByLabelText(/Логин/), { target: { value: 'kovaleva' } });
    fireEvent.change(screen.getByLabelText(/Пароль/), { target: { value: 'secret-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }));

    expect(await screen.findByText('Заявка ждёт подтверждения координатора')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Регистрация' })).toBeTruthy();
    await waitFor(() => expect(posts('/api/auth/login')).toHaveLength(1));
  });
});

vi.setConfig({ testTimeout: 15_000 });
