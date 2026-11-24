import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { CoverageRow, CoverageView } from './api';
import { CoveragePage } from './CoveragePage';

const row = (name: string, state: CoverageRow['state'], outdated = false): CoverageRow => ({
  user: { id: name, name, role: 'Designer', disciplines: ['АР'], position: null }, state, toadTools: state === 'Never' ? null : outdated ? '4.6.0' : '4.7.0',
  outdated, lastSessionAt: state === 'Never' ? null : '2026-11-20T07:00:00Z', machines: state === 'Never' ? [] : ['WS-01'],
});

const view: CoverageView = {
  minToadTools: '4.6.0', latestToadTools: '4.7.0',
  rows: [row('Агеев', 'Never'), row('Борисов', 'Active'), row('Волков', 'Gone', true), row('Гусев', 'Active', true)],
};

function render() {
  renderUi(<AuthProvider><CoveragePage /></AuthProvider>, {
    '/api/usage/filters': { people: [], machines: [], disciplines: [], revitYears: [] },
    '/api/usage/coverage': view,
  });
}

const names = () => screen.queryAllByText(/^(Агеев|Борисов|Волков|Гусев)$/).map((e) => e.textContent);

describe('CoveragePage', () => {
  it('counts accounts in each state', async () => {
    render();

    expect(await screen.findByText('Нет данных ни разу: 1 из 4')).toBeTruthy();
    expect(screen.getByText('Пропал больше 14 дней: 1 из 4')).toBeTruthy();
    expect(screen.getByText('Старая версия ToadTools: 2 из 4')).toBeTruthy();
  });

  it('toggles filter by state and by old version', async () => {
    render();
    await screen.findByText('Агеев');
    expect(names()).toEqual(['Агеев', 'Борисов', 'Волков', 'Гусев']);

    fireEvent.click(screen.getByRole('checkbox', { name: 'Нет данных ни разу' }));
    await waitFor(() => expect(names()).toEqual(['Агеев']));

    fireEvent.click(screen.getByRole('checkbox', { name: 'Нет данных ни разу' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Старая версия ToadTools' }));
    await waitFor(() => expect(names()).toEqual(['Волков', 'Гусев']));
  });
});
