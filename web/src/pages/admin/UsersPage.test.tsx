import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const posts = (path: string) => calls().filter(([url, init]) => url === path && init?.method === 'POST');
const puts = (path: string) => calls().filter(([url, init]) => url === path && init?.method === 'PUT');

const person = (id: string, displayName: string, role: string, extra: object = {}) => ({
  id, login: id, displayName, role, isDisabled: false, lastLoginAt: null, isPending: false, position: null, email: null, phone: null,
  disciplines: [], createdAt: '2026-10-01T00:00:00Z', ...extra,
});

const coordinator = person('u1', 'Координатор Иванов', 'Operator');
const olga = person('u3', 'Ольга Ковалёва', 'Designer', { isPending: true, disciplines: ['ОВ'], position: 'Инженер ОВ', email: 'olga@example.com' });

const responses = {
  '/api/auth/me': coordinator,
  '/api/users': [coordinator, person('u2', 'Пётр', 'Designer', { login: 'petr', disciplines: ['КР'] }), olga],
  '/api/users/pending': [olga],
  '/api/auth/disciplines': ['АР', 'ВК', 'КР', 'ОВ'],
  'POST /api/users/u3/approve': olga,
  'POST /api/users/u3/reject': null,
  'PUT /api/users/u2/disciplines': null,
  'PUT /api/users/u2/contacts': null,
};

describe('Users for coordinator', () => {
  it('Users_OperatorApprovesWithDisciplines', async () => {
    renderApp('/admin/users', responses);

    const request = (await screen.findByText('Ольга Ковалёва')).closest('[data-request]') as HTMLElement;
    expect(within(request).getByText(/Инженер ОВ/)).toBeTruthy();
    fireEvent.click(within(request).getAllByLabelText('Разделы').find((e) => (e as HTMLInputElement).type !== 'hidden')!);
    fireEvent.click(await within(request).findByRole('option', { name: 'ВК' }));
    fireEvent.click(within(request).getByRole('button', { name: 'Подтвердить' }));

    await waitFor(() => expect(posts('/api/users/u3/approve')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/users/u3/approve')[0][1]!.body))).toEqual({ disciplines: ['ОВ', 'ВК'] });
  });

  it('Users_ClickOpensCard_OperatorEditsContactsNotRole', async () => {
    renderApp('/admin/users', responses);

    fireEvent.click(await screen.findByText('Пётр'));
    const card = await screen.findByRole('dialog', { name: 'Пётр' });
    expect(within(card).getByText(/petr/)).toBeTruthy();
    expect(within(card).queryByLabelText('Роль')).toBeNull();
    expect(within(card).queryByRole('button', { name: 'Сбросить пароль' })).toBeNull();
    expect(screen.queryByRole('button', { name: '+ Добавить' })).toBeNull();
    fireEvent.change(within(card).getByLabelText('Должность'), { target: { value: 'Инженер КР' } });
    fireEvent.change(within(card).getByLabelText('Почта'), { target: { value: 'petr@example.com' } });
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(puts('/api/users/u2/contacts')).toHaveLength(1));
    expect(JSON.parse(String(puts('/api/users/u2/contacts')[0][1]!.body)))
      .toEqual({ displayName: 'Пётр', position: 'Инженер КР', email: 'petr@example.com', phone: null });
    expect(puts('/api/users/u2/disciplines')).toHaveLength(0); // разделы не менялись
    expect(puts('/api/users/u2')).toHaveLength(0);
  });

  it('Users_AdminCardChangesRoleAndActive', async () => {
    renderApp('/admin/users', { ...responses, '/api/auth/me': { ...coordinator, role: 'Admin' }, 'PUT /api/users/u2': null });

    fireEvent.click(await screen.findByText('Пётр'));
    const card = await screen.findByRole('dialog', { name: 'Пётр' });
    expect(within(card).getByRole('button', { name: 'Сбросить пароль' })).toBeTruthy();
    expect(within(card).getByRole('button', { name: 'Удалить' })).toBeTruthy();
    fireEvent.click(within(card).getByLabelText('Активен'));
    fireEvent.click(within(card).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(puts('/api/users/u2')).toHaveLength(1));
    expect(JSON.parse(String(puts('/api/users/u2')[0][1]!.body))).toEqual({ displayName: 'Пётр', role: 'Designer', isDisabled: true });
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Users table', () => {
  const people = [
    person('a1', 'Анна', 'Admin', { lastLoginAt: '2026-10-05T10:00:00Z', position: 'ГИП' }),
    person('b2', 'Борис', 'Designer', { lastLoginAt: '2026-10-01T10:00:00Z', disciplines: ['ОВ'] }),
    person('c3', 'Вера', 'Designer', { lastLoginAt: null, login: 'vera', disciplines: ['АР'] }),
    person('d4', 'Глеб', 'Viewer', { lastLoginAt: '2026-10-09T10:00:00Z' }),
  ];
  const table = { ...responses, '/api/users': people, '/api/users/pending': [] };
  const names = () => within(screen.getByRole('table')).getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[1].textContent);

  it('users are sorted by last login when its header is clicked', async () => {
    renderApp('/admin/users', table);
    await screen.findByText('Анна');

    fireEvent.click(screen.getByRole('button', { name: /Последний вход/ }));

    expect(names()[0]).toMatch(/^Борис/);
    expect(names()[2]).toMatch(/^Глеб/);
    expect(names()[3]).toMatch(/^Вера/); // никогда не входила — в конце
  });

  it('role filter and search combine', async () => {
    renderApp('/admin/users?f.role=Designer&q=vera', table);

    await screen.findByText('Вера');
    expect(names()).toHaveLength(1);
  });

  it('bulk "Добавить раздел" posts the selected ids and shows skipped users', async () => {
    renderApp('/admin/users', { ...table, 'POST /api/users/bulk': { changed: 1, skipped: ['Вера: заявка ещё не подтверждена'] } });
    await screen.findByText('Анна');

    const rows = within(screen.getByRole('table')).getAllByRole('row');
    fireEvent.click(within(rows[2]).getByRole('checkbox'));
    fireEvent.click(within(rows[3]).getByRole('checkbox'));
    fireEvent.click(screen.getByRole('button', { name: 'Добавить раздел' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('option', { name: 'КР', hidden: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(posts('/api/users/bulk')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/users/bulk')[0][1]!.body))).toEqual({ ids: ['b2', 'c3'], action: 'AddDiscipline', discipline: 'КР' });
    expect(await screen.findByText('Вера: заявка ещё не подтверждена')).toBeTruthy();
  });

  it('bulk disable is hidden from an operator', async () => {
    renderApp('/admin/users', table);
    await screen.findByText('Анна');

    fireEvent.click(within(within(screen.getByRole('table')).getAllByRole('row')[1]).getByRole('checkbox'));

    expect(screen.getByRole('button', { name: 'Добавить раздел' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Отключить' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Включить' })).toBeNull();
  });
});
