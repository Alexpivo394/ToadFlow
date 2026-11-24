import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { ServiceView } from './api';
import { ServicePage } from './ServicePage';

const view: ServiceView = {
  basis: { people: 2, machines: 0 },
  web: {
    days: [{ day: '2026-11-24', people: 2 }],
    spaces: [{ space: 'clash', visits: 5, people: 2 }],
    pages: [{ space: 'clash', page: '/clash/checks', visits: 4, people: 2 }, { space: 'clash', page: '/clash/my', visits: 1, people: 1 }],
    roles: [{ role: 'Designer', people: 1, visits: 3 }],
  },
  plugins: { areas: [{ area: 'families', calls: 12, people: 1 }], users: [{ id: 'u1', name: 'Иванов', areas: [{ area: 'families', calls: 12 }] }] },
};

const render = (answer: ServiceView) =>
  renderUi(<AuthProvider><ServicePage /></AuthProvider>, {
    '/api/usage/filters': { people: [], machines: [], disciplines: [], revitYears: [] },
    '/api/usage/service': answer,
  }, '/usage/service');

describe('ServicePage', () => {
  it('shows web pages, roles and plugin calls', async () => {
    render(view);

    expect(await screen.findByText('/clash/checks')).toBeTruthy();
    expect(screen.getByText('Проектировщик')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: 'Плагины' }));

    expect(await screen.findByText('Иванов')).toBeTruthy();
    expect(screen.getByText('families: 12')).toBeTruthy();
  });

  it('empty service explains where data comes from', async () => {
    render({ basis: { people: 0, machines: 0 }, web: { days: [], spaces: [], pages: [], roles: [] }, plugins: { areas: [], users: [] } });

    expect(await screen.findByText('Данных пока нет')).toBeTruthy();
  });
});
