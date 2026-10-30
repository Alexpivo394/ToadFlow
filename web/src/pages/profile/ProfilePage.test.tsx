import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const sent = (method: string, path: string) => calls().filter(([url, init]) => url === path && init?.method === method);

const me = {
  id: 'u1', login: 'ovk', displayName: 'Ольга', role: 'Designer', isDisabled: false, lastLoginAt: null, isPending: false,
  position: 'Инженер', email: null, phone: null, disciplines: ['ОВ', 'ВК'], createdAt: '2026-10-01T00:00:00Z',
};

const responses = {
  '/api/auth/me': me,
  '/api/profile': me,
  'PUT /api/profile': { ...me, position: 'ГИП' },
  'POST /api/profile/password': null,
  '/api/clash/my': { total: 0, items: [] },
  '/api/clash/my/summary': { disciplines: ['ОВ', 'ВК'], toFix: 0, takenByOther: 0, newSinceVisit: 0, previousSeenAt: null },
  'POST /api/clash/my/visit': null,
  '/api/clash/checks': [],
};

describe('Profile', () => {
  it('Profile_EditsOwn_DisciplinesReadOnly', async () => {
    renderApp('/profile', responses);

    const position = (await screen.findByLabelText('Должность')) as HTMLInputElement;
    expect(position.value).toBe('Инженер');
    expect(screen.getByText('ВК')).toBeTruthy();
    expect(screen.getByText(/Разделы меняет координатор/)).toBeTruthy();
    expect(screen.queryByLabelText(/Разделы/)).toBeNull();
    fireEvent.change(position, { target: { value: 'ГИП' } });
    fireEvent.change(screen.getByLabelText('Телефон'), { target: { value: '+7 900' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(sent('PUT', '/api/profile')).toHaveLength(1));
    expect(JSON.parse(String(sent('PUT', '/api/profile')[0][1]!.body))).toEqual({ displayName: 'Ольга', position: 'ГИП', email: null, phone: '+7 900' });
  });

  it('Profile_ChangePassword', async () => {
    renderApp('/profile', responses);

    fireEvent.change(await screen.findByLabelText('Текущий пароль'), { target: { value: 'old-pass-1' } });
    fireEvent.change(screen.getByLabelText('Новый пароль'), { target: { value: 'new-pass-2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сменить пароль' }));

    await waitFor(() => expect(sent('POST', '/api/profile/password')).toHaveLength(1));
    expect(JSON.parse(String(sent('POST', '/api/profile/password')[0][1]!.body))).toEqual({ current: 'old-pass-1', new: 'new-pass-2' });
  });
});

vi.setConfig({ testTimeout: 15_000 });
