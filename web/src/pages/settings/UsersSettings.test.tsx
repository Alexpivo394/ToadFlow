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
    renderApp('/settings/users', responses);

    const request = (await screen.findByText('Ольга Ковалёва')).closest('[data-request]') as HTMLElement;
    expect(within(request).getByText(/Инженер ОВ/)).toBeTruthy();
    fireEvent.click(within(request).getAllByLabelText('Разделы').find((e) => (e as HTMLInputElement).type !== 'hidden')!);
    fireEvent.click(await within(request).findByRole('option', { name: 'ВК' }));
    fireEvent.click(within(request).getByRole('button', { name: 'Подтвердить' }));

    await waitFor(() => expect(posts('/api/users/u3/approve')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/users/u3/approve')[0][1]!.body))).toEqual({ disciplines: ['ОВ', 'ВК'] });
  });

  it('Users_ClickOpensCard_OperatorEditsContactsNotRole', async () => {
    renderApp('/settings/users', responses);

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
    renderApp('/settings/users', { ...responses, '/api/auth/me': { ...coordinator, role: 'Admin' }, 'PUT /api/users/u2': null });

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
