import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const naming = {
  separators: '_',
  entries: [{ field: 'object', tokens: 'POL', value: 'Полковая', customer: 'ГК Пионер' }, { field: 'discipline', tokens: 'AR', value: 'АР' }],
  patterns: [{ field: 'part', pattern: '^K(\d+)$', value: 'корпус $1' }],
  nameExpressions: [],
};

const responses = {
  '/api/naming': naming,
  'PUT /api/naming': naming,
  '/api/naming/models': [{ name: 'POL_AR_RLP_K1_R22', path: 'RSN://rs22/POL/POL_AR_RLP_K1_R22.rvt' }],
  '/api/naming/preview': [
    { name: 'POL_AR_RLP_K1_R22', result: { customer: 'ГК Пионер', object: 'Полковая', discipline: 'АР', part: 'корпус 1', fields: {}, unrecognized: ['RLP'] } },
  ],
};

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

describe('NamingPage', () => {
  it('naming page reads and saves through /api/naming', async () => {
    renderApp('/farm/naming', responses);

    expect(await screen.findByDisplayValue('Полковая')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Именование моделей' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить справочник' }));

    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/naming' && init?.method === 'PUT')).toBe(true));
    expect(calls().some(([url]) => url.startsWith('/api/loi/naming'))).toBe(false);
  });

  it('dictionary by fields and preview with unrecognized pieces', async () => {
    renderApp('/farm/naming', responses);

    expect(await screen.findByDisplayValue('Полковая')).toBeTruthy();
    expect(screen.getByDisplayValue('ГК Пионер')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Модели сервиса' }));
    expect(await screen.findByDisplayValue('POL_AR_RLP_K1_R22')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Разобрать' }));
    expect(await screen.findByText('RLP')).toBeTruthy();
    expect(screen.getByText('корпус 1')).toBeTruthy();
  });

  it('operator sees but cannot change', async () => {
    renderApp('/farm/naming', { ...responses, '/api/auth/me': { id: 'u2', login: 'op', displayName: 'Оператор', role: 'Operator', isDisabled: false, lastLoginAt: null } });

    expect(await screen.findByDisplayValue('Полковая')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сохранить справочник' })).toBeNull();
  });
});
