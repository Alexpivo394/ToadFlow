import { screen } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { Failure, renderUi } from '../../test/renderApp';
import type { PersonView, SummaryView } from './api';
import { PersonPage } from './PersonPage';
import { SummaryPage } from './SummaryPage';

const filters = { '/api/usage/filters': { people: [], machines: [], disciplines: [], revitYears: [] } };

const summary = (previousWait: number): SummaryView => ({
  basis: { people: 12, machines: 9 },
  totals: { waitMs: 45_000_000, abnormalSessions: 3, people: 12, toadToolsRuns: 140 },
  previous: { waitMs: previousWait, abnormalSessions: 0, people: 10, toadToolsRuns: 100 },
  days: [{ day: '2026-11-24', openMs: 3_600_000, linkMs: 0, syncMs: 1_800_000, saveMs: 0 }],
  slowModels: [{ key: 'rsn://srv/п/ар.rvt', title: 'АР.rvt', medianMs: 845_000, count: 4 }],
  slowMachines: [{ key: 'ws-01', name: 'WS-01', totalMs: 7_200_000 }],
  crashingMachines: [],
});

describe('SummaryPage', () => {
  it('tiles show the change to the previous period, none when it was zero', async () => {
    renderUi(<AuthProvider><SummaryPage /></AuthProvider>, { ...filters, '/api/usage/summary': summary(40_000_000) });

    expect(await screen.findByText('12,5 ч')).toBeTruthy();
    expect(screen.getByText('+13 % к прошлому периоду')).toBeTruthy();
    expect(screen.getByText('+20 % к прошлому периоду')).toBeTruthy();
    expect(screen.getByText('+40 % к прошлому периоду')).toBeTruthy();
    // Аварий в прошлом периоде не было — процента нет.
    expect(screen.getAllByText(/к прошлому периоду/)).toHaveLength(3);
    expect(screen.getByText('АР.rvt')).toBeTruthy();
    expect(screen.getByText('14 мин 05 с')).toBeTruthy();
  });

  it('empty summary explains where data comes from', async () => {
    renderUi(<AuthProvider><SummaryPage /></AuthProvider>, {
      ...filters,
      '/api/usage/summary': {
        ...summary(0), basis: { people: 0, machines: 0 }, totals: { waitMs: 0, abnormalSessions: 0, people: 0, toadToolsRuns: 0 },
      },
    });

    expect(await screen.findByText('Данных пока нет')).toBeTruthy();
  });
});

describe('PersonPage', () => {
  const person: PersonView = {
    user: { id: 'u1', name: 'Иванов', role: 'Designer', position: 'Архитектор', disciplines: ['АР'] },
    machines: [{ key: 'ws-01', name: 'WS-01' }],
    days: [{ day: '2026-11-24', activeSeconds: 3600, sessions: 1 }],
    waits: { totalMs: 9000, rows: [{ kind: 'Open', count: 2, medianMs: 2000 }] },
    commands: [{ source: 'Revit', commandId: 'ID_X', name: 'Стена', count: 9 }],
    web: [{ space: 'clash', visits: 4 }],
    plugins: [],
  };

  const render = (answer: unknown) =>
    renderUi(<AuthProvider><Routes><Route path="/usage/people/:id" element={<PersonPage />} /></Routes></AuthProvider>,
      { ...filters, '/api/usage/people/u1': answer }, '/usage/people/u1');

  it('shows machines, waits, commands and web', async () => {
    render(person);

    expect(await screen.findByRole('heading', { name: 'Иванов' })).toBeTruthy();
    expect(screen.getByText('Проектировщик · Архитектор · АР')).toBeTruthy();
    expect(screen.getByText('WS-01')).toBeTruthy();
    expect(screen.getByText('Стена')).toBeTruthy();
    expect(screen.getByText('clash: 4')).toBeTruthy();
  });

  it('unknown account says so', async () => {
    render(new Failure(404, 'нет'));

    expect(await screen.findByText('Такой учётки нет')).toBeTruthy();
  });
});
