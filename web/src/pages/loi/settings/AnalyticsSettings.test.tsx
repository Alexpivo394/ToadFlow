import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const responses = {
  '/api/loi/analytics/settings': { enabled: true, retentionYears: 0 },
  '/api/loi/models/overrides': [],
  '/api/naming/models': [],
  '/api/loi/analytics/filters': { customers: [], objects: [], disciplines: [], parts: [], profiles: [], first: null, last: null, fields: {} },
  '/api/loi/disciplines': [],
};

describe('AnalyticsSettings', () => {
  it('LOI analytics settings no longer contain the naming editor', async () => {
    renderApp('/loi/analytics-settings', responses);

    expect(await screen.findByText('Ручные поправки моделей')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сохранить справочник' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Разобрать' })).toBeNull();
  });
});

describe('Tokens', () => {
  // Токены плагинам выдаёт окно входа в плагине — страницы в вебе нет.
  it('Settings_NoTokensPage', async () => {
    renderApp('/settings/tokens', {});

    expect(await screen.findByRole('group', { name: 'Пространства' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Токены доступа' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Создать токен' })).toBeNull();
  });
});
