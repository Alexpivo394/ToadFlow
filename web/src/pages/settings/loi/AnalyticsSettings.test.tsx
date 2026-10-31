import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const naming = {
  separators: '_',
  entries: [{ field: 'object', tokens: 'POL', value: 'Полковая', customer: 'ГК Пионер' }, { field: 'discipline', tokens: 'AR', value: 'АР' }],
  patterns: [{ field: 'part', pattern: '^K(\\d+)$', value: 'корпус $1' }],
  nameExpressions: [],
};

const responses = {
  '/api/loi/analytics/settings': { enabled: true, retentionYears: 0 },
  '/api/loi/naming': naming,
  '/api/loi/models/overrides': [],
  '/api/loi/naming/models': [{ name: 'POL_AR_RLP_K1_R22', path: 'RSN://rs22/POL/POL_AR_RLP_K1_R22.rvt' }],
  '/api/loi/naming/preview': [
    { name: 'POL_AR_RLP_K1_R22', result: { customer: 'ГК Пионер', object: 'Полковая', discipline: 'АР', part: 'корпус 1', fields: {}, unrecognized: ['RLP'] } },
  ],
};

describe('AnalyticsSettings', () => {
  it('dictionary by fields and preview with unrecognized pieces', async () => {
    renderApp('/settings/analytics', responses);

    expect(await screen.findByDisplayValue('Полковая')).toBeTruthy();
    expect(screen.getByDisplayValue('ГК Пионер')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Модели сервиса' }));
    expect(await screen.findByDisplayValue('POL_AR_RLP_K1_R22')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Разобрать' }));
    expect(await screen.findByText('RLP')).toBeTruthy();
    expect(screen.getByText('корпус 1')).toBeTruthy();
  });

  it('operator sees but cannot change', async () => {
    renderApp('/settings/analytics', { ...responses, '/api/auth/me': { id: 'u2', login: 'op', displayName: 'Оператор', role: 'Operator', isDisabled: false, lastLoginAt: null } });

    expect(await screen.findByDisplayValue('Полковая')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сохранить справочник' })).toBeNull();
  });
});

describe('Tokens', () => {
  // Токены плагинам выдаёт окно входа в плагине — страницы в вебе нет.
  it('Settings_NoTokensPage', async () => {
    renderApp('/settings/tokens', {});

    expect(await screen.findByRole('link', { name: 'Агенты' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Токены доступа' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Создать токен' })).toBeNull();
  });
});
