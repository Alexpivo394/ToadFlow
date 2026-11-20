import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const server = {
  version: '3.0.0', startedAt: '2026-11-10T10:00:00Z', databaseBytes: 4.2 * 1024 ** 3,
  volumes: [{ name: 'Сервер', freeBytes: 61 * 1024 ** 3, totalBytes: 200 * 1024 ** 3 }], processMemoryBytes: 900 * 1024 ** 2,
  workers: [{ module: 'clash', title: 'Движок коллизий', lastSeenAt: '2026-11-21T09:59:00Z', online: true }],
};

const tiles = [
  { module: 'export', value: '142', label: 'за сутки', trend: null, lines: ['ошибок: 3', 'следующий запуск 21.11 22:00'], link: '/export/schedules' },
  { module: 'loi', value: '—', label: 'нет данных', trend: null, lines: [], link: '/loi' },
  { module: 'clash', value: '318', label: 'открыто', trend: null, lines: ['новых за неделю: 24'], link: '/clash/summary' },
];

const overview = (attention: object[] = []) => ({ attention, tiles, server });

const agents = [
  {
    id: 'a1', machineName: 'BIM-FARM-01', ip: null, agentVersion: '1.0.3', totalRamMb: 65536, state: 'Online', isDraining: false,
    lastSeenAt: new Date().toISOString(), cpuPercent: 64, ramUsedMb: 41000, revits: [{ year: 2022, hasNwcExporter: true }],
    limits: [{ year: 2022, maxInstances: 1 }], isOutdated: false,
    instances: [{ slot: 1, year: 2022, pid: 1, state: 'Busy', jobId: 'j9', stage: 'Экспорт NWC', stageSince: new Date().toISOString(), modelName: 'ЖК-Север_АР_К1.rvt' }],
  },
];

const responses = (attention: object[] = []) => ({
  '/api/overview': overview(attention),
  '/api/agents': agents,
  '/api/settings': { silenceTimeoutMinutes: 10 },
});

const operator = { id: 'u2', login: 'op', displayName: 'Координатор', role: 'Operator', isDisabled: false, lastLoginAt: null };

describe('OverviewPage', () => {
  it('shows one green line "Всё в порядке" when there is nothing to attend', async () => {
    renderApp('/', responses());

    expect(await screen.findByText('Всё в порядке')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Обзор' })).toBeTruthy();
  });

  it('each attention line links to its place; problems come before todos', async () => {
    renderApp('/', responses([
      { level: 'Todo', text: '2 заявки на регистрацию', link: '/admin/users', module: null, minRole: 'Operator' },
      { level: 'Problem', text: 'Агент BIM-PC-03 не в сети 2 ч', link: '/farm/agents', module: null, minRole: 'Viewer' },
    ]));

    const block = await screen.findByRole('region', { name: 'Требует внимания' });
    const links = await within(block).findAllByRole('link');
    expect(links.map((l) => [l.textContent, l.getAttribute('href')])).toEqual([
      ['Агент BIM-PC-03 не в сети 2 ч', '/farm/agents'],
      ['2 заявки на регистрацию', '/admin/users'],
    ]);
  });

  it('renders a tile per tile in the response and links it to the module', async () => {
    renderApp('/', responses());

    const exportTile = await within(await screen.findByRole('main')).findByRole('link', { name: /Выгрузка/ });
    expect(exportTile.getAttribute('href')).toBe('/export/schedules');
    expect(within(exportTile).getByText('142')).toBeTruthy();
    expect(within(exportTile).getByText('ошибок: 3')).toBeTruthy();
    expect(within(screen.getByRole('main')).getByRole('link', { name: /Коллизии/ }).getAttribute('href')).toBe('/clash/summary');
  });

  it('a "нет данных" tile is shown muted and still links to the module', async () => {
    renderApp('/', responses());

    const loiTile = await within(await screen.findByRole('main')).findByRole('link', { name: /Проверка LOI/ });
    expect(loiTile.getAttribute('href')).toBe('/loi');
    expect(within(loiTile).getByText('нет данных')).toBeTruthy();
    expect(loiTile.getAttribute('data-muted')).toBe('true');
  });

  it('server block shows version, database size, free space and workers', async () => {
    renderApp('/', responses());

    const block = await screen.findByRole('region', { name: 'Сервер' });
    expect(await within(block).findByText(/3\.0\.0/)).toBeTruthy();
    expect(within(block).getByText(/4,2 ГБ/)).toBeTruthy();
    expect(within(block).getByText(/свободно 61 ГБ/)).toBeTruthy();
    expect(within(block).getByText(/Движок коллизий: в сети/)).toBeTruthy();
  });

  it('agents panel keeps "Отменить" and "Убить" for an operator', async () => {
    renderApp('/', { ...responses(), '/api/auth/me': operator });

    expect(await screen.findByText(/Обрабатывает ЖК-Север_АР_К1\.rvt/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Отменить' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Убить' })).toBeTruthy();
  });
});
