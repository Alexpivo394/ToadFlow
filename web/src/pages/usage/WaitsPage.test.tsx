import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { WaitCase, WaitsView } from './api';
import { WaitsPage } from './WaitsPage';

const waits: WaitsView = {
  basis: { people: 3, machines: 2 },
  rows: [
    { key: 'rsn://srv01/п/ар.rvt', title: 'АР.rvt', count: 12, medianMs: 845_000, p90Ms: 1_500_000, totalMs: 45_000_000, failed: 1, trend: [800_000, null, 900_000] },
    { key: 'rsn://srv01/п/кр.rvt', title: 'КР.rvt', count: 3, medianMs: 42_000, p90Ms: 60_000, totalMs: 126_000, failed: 0, trend: [] },
  ],
};

const cases: WaitCase[] = [{
  id: 'c1', kind: 'Open', startedAt: '2026-11-24T07:00:00Z', durationMs: 845_000, outcome: 'Succeeded', user: { id: 'u1', name: 'Иванов' },
  machine: { key: 'ws-01', name: 'WS-01' }, modelName: 'АР.rvt', modelPath: 'RSN://srv01/П/АР.rvt', hostModel: null, freeMemoryMb: 2048, fileSizeBytes: null,
}];

function Where() {
  return <div data-testid="where">{useLocation().search}</div>;
}

function render(answer: WaitsView = waits, path = '/usage/waits') {
  const requested: string[] = [];
  renderUi(<AuthProvider><WaitsPage /><Where /></AuthProvider>, {
    '/api/usage/filters': { people: [], machines: [], disciplines: [], revitYears: [] },
    '/api/usage/waits': (url: string) => {
      requested.push(url);
      return answer;
    },
    '/api/usage/waits/cases': (url: string) => {
      requested.push(url);
      return cases;
    },
  }, path);
  return requested;
}

describe('WaitsPage', () => {
  it('shows rows with formatted durations and the basis', async () => {
    render();

    expect(await screen.findByText('АР.rvt')).toBeTruthy();
    expect(screen.getByText('14 мин 05 с')).toBeTruthy();
    expect(screen.getByText('12,5 ч')).toBeTruthy();
    expect(screen.getByText('Построено по 3 людям и 2 машинам')).toBeTruthy();
  });

  it('switching the tab changes the group in the request and the address', async () => {
    const requested = render();
    await screen.findByText('АР.rvt');

    fireEvent.click(screen.getByRole('tab', { name: 'По машинам' }));

    await waitFor(() => expect(requested.some((url) => url.includes('group=machine'))).toBe(true));
    expect(screen.getByTestId('where').textContent).toContain('tab=machine');
  });

  it('opening a row requests its cases', async () => {
    const requested = render();

    fireEvent.click(await screen.findByText('АР.rvt'));

    expect(await screen.findByText('Иванов')).toBeTruthy();
    expect(requested.some((url) => url.startsWith('/api/usage/waits/cases') && url.includes('key=rsn'))).toBe(true);
  });

  it('empty answer explains where the data comes from', async () => {
    render({ basis: { people: 0, machines: 0 }, rows: [] });

    expect(await screen.findByText('Данных пока нет')).toBeTruthy();
  });

  it('empty links tab explains why links may be missing', async () => {
    render({ basis: { people: 0, machines: 0 }, rows: [] }, '/usage/waits?tab=link');

    expect(await screen.findByText(/время входит в открытие модели/)).toBeTruthy();
  });
});
