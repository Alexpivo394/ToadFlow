import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const posts = (path: string) => calls().filter(([url, init]) => url === path && init?.method === 'POST');

const person = (id: string, displayName: string, role: string, extra: object = {}) => ({
  id, login: id, displayName, role, isDisabled: false, lastLoginAt: null, isPending: false, position: null, email: null, phone: null,
  disciplines: [], createdAt: '2026-10-01T00:00:00Z', ...extra,
});

const coordinator = person('u1', 'Координатор Иванов', 'Operator');
const olga = person('u3', 'Ольга Ковалёва', 'Designer', { isPending: true, disciplines: ['ОВ'], position: 'Инженер ОВ', email: 'olga@example.com' });

const responses = {
  '/api/auth/me': coordinator,
  '/api/users': [coordinator, person('u2', 'Пётр', 'Designer', { disciplines: ['КР'] }), olga],
  '/api/users/pending': [olga],
  '/api/auth/disciplines': ['АР', 'ВК', 'КР', 'ОВ'],
  'POST /api/users/u3/approve': olga,
  'POST /api/users/u3/reject': null,
  'PUT /api/users/u2/disciplines': null,
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

  it('Users_OperatorCannotChangeRole', async () => {
    renderApp('/settings/users', responses);

    const row = (await screen.findByText('Пётр')).closest('tr') as HTMLElement;
    expect(within(row).getByText('Проектировщик')).toBeTruthy();
    expect(within(row).queryByLabelText('Роль')).toBeNull();
    expect(within(row).queryByRole('button', { name: 'Сбросить пароль' })).toBeNull();
    expect(screen.queryByRole('button', { name: '+ Добавить' })).toBeNull();
    expect(within(row).getAllByLabelText('Разделы').length).toBeGreaterThan(0);
  });
});

vi.setConfig({ testTimeout: 15_000 });
