import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { MachineCard, MachineRow, MachinesView } from './api';
import { MachinePage } from './MachinePage';
import { MachinesPage } from './MachinesPage';
import { Route, Routes } from 'react-router';

const row = (key: string, openMedianMs: number | null, extra: Partial<MachineRow> = {}): MachineRow => ({
  key, name: key.toUpperCase(), cpu: 'Intel Core i7', cores: 8, ramMb: 32768, gpu: 'RTX A2000', gpuMemoryMb: 6144, diskKind: 'Ssd', diskTotalGb: 512,
  diskFreeGb: 120, os: 'Windows 11', revitYears: [2022, 2024], toadTools: '4.6.0', lastSeenAt: '2026-11-24T07:00:00Z',
  lastUser: { id: 'u1', name: 'Иванов' }, openMedianMs, syncMedianMs: 30_000, sessions: 4, abnormal: 1, activeHours: 12.5, ...extra,
});

const filters = { '/api/usage/filters': { people: [], machines: [], disciplines: [], revitYears: [] } };

describe('MachinesPage', () => {
  it('lists the fleet slowest first', async () => {
    const view: MachinesView = { basis: { people: 2, machines: 3 }, rows: [row('ws-02', 30_000), row('ws-01', 845_000), row('ws-03', null)] };
    renderUi(<AuthProvider><MachinesPage /></AuthProvider>, { ...filters, '/api/usage/machines': view });

    const names = await screen.findAllByText(/^WS-0\d$/);
    expect(names.map((n) => n.textContent)).toEqual(['WS-01', 'WS-02', 'WS-03']);
    expect(screen.getByText('14 мин 05 с')).toBeTruthy();
    expect(screen.getAllByText('32 ГБ').length).toBe(3);
  });

  it('empty fleet explains where data comes from', async () => {
    renderUi(<AuthProvider><MachinesPage /></AuthProvider>, { ...filters, '/api/usage/machines': { basis: { people: 0, machines: 0 }, rows: [] } });

    expect(await screen.findByText('Данных пока нет')).toBeTruthy();
  });
});

describe('MachinePage', () => {
  it('card shows hardware, people and crashes with the caveat', async () => {
    const card: MachineCard = {
      ...row('ws-01', 845_000), firstSeenAt: '2026-10-01T07:00:00Z',
      users: [{ id: 'u1', name: 'Иванов', sessions: 3 }],
      abnormalSessions: [{ id: 's1', startedAt: '2026-11-20T07:00:00Z', seenAt: '2026-11-20T09:00:00Z', user: { id: 'u1', name: 'Иванов' } }],
      weeks: [{ start: '2026-11-01', openMedianMs: 600_000, syncMedianMs: 20_000 }, { start: '2026-11-08', openMedianMs: 845_000, syncMedianMs: null }],
    };
    renderUi(<AuthProvider><Routes><Route path="/usage/machines/:key" element={<MachinePage />} /></Routes></AuthProvider>,
      { ...filters, '/api/usage/machines/ws-01': card }, '/usage/machines/ws-01');

    expect(await screen.findByRole('heading', { name: 'WS-01' })).toBeTruthy();
    expect(screen.getByText(/Intel Core i7/)).toBeTruthy();
    expect(screen.getAllByText('Иванов').length).toBeGreaterThan(0);
    expect(screen.getByText('Вылет, снятие процесса или выключение компьютера — различить нельзя.')).toBeTruthy();
  });
});
