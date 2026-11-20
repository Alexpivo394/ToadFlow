import { screen, waitFor } from '@testing-library/react';
import { useLocation } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { Failure, renderApp } from '../test/renderApp';

vi.setConfig({ testTimeout: 15_000 });

vi.mock('../layout/AppShell', async () => {
  const { Outlet } = await import('react-router');
  // Оболочка без меню: тест смотрит только, куда привёл адрес.
  return {
    AppShell: () => {
      const location = useLocation();
      return (
        <>
          <div data-testid="where">{location.pathname + location.search + location.hash}</div>
          <Outlet />
        </>
      );
    },
  };
});

vi.mock('../spaces/registry', async () => {
  const actual = await vi.importActual<typeof import('../spaces/registry')>('../spaces/registry');
  const { Route } = await import('react-router');
  // Страницы пространств — заглушки: важен адрес, а не их содержимое.
  return {
    allSpaces: actual.allSpaces.map((space) => ({ ...space, routes: () => <Route path="*" element={<div>страница</div>} /> })),
  };
});

const where = () => screen.getByTestId('where').textContent;

const schedule = (id: string, module: string) => ({ id, module, moduleKind: module === 'clash' ? 'clash-extract' : module });

const responses = {
  '/api/schedules/s-loi': schedule('s-loi', 'loi'),
  '/api/schedules/s-clash': schedule('s-clash', 'clash'),
  '/api/schedules/gone': new Failure(404, 'нет'),
};

const table: [string, string][] = [
  ['/schedules', '/export/schedules'],
  ['/history', '/farm/queue'],
  ['/history/models', '/farm/queue/models'],
  ['/history/runs/r1', '/farm/queue/runs/r1'],
  ['/history/events', '/admin/events'],
  ['/analytics', '/loi/analytics'],
  ['/analytics/models', '/loi/analytics/models'],
  ['/settings', '/farm/agents'],
  ['/settings/agents', '/farm/agents'],
  ['/settings/sources', '/farm/sources'],
  ['/settings/presets', '/export/presets'],
  ['/settings/loi', '/loi/profiles'],
  ['/settings/analytics', '/loi/analytics-settings'],
  ['/settings/clash', '/clash/setup/settings'],
  ['/settings/users', '/admin/users'],
  ['/settings/general', '/admin/server'],
];

describe('old addresses', () => {
  it.each(table)('%s → %s', async (from, to) => {
    renderApp(from, responses);

    await waitFor(() => expect(where()).toBe(to));
  });

  it('keeps the query string', async () => {
    renderApp('/history?status=Failed#top', responses);

    await waitFor(() => expect(where()).toBe('/farm/queue?status=Failed#top'));
  });

  it('old schedule link goes to its module', async () => {
    const loi = renderApp('/schedules/s-loi', responses);
    await waitFor(() => expect(where()).toBe('/loi/schedules/s-loi'));
    loi.unmount();

    const clash = renderApp('/schedules/s-clash', responses);
    await waitFor(() => expect(where()).toBe('/clash/setup/schedules/s-clash'));
    clash.unmount();

    renderApp('/schedules/gone', responses);
    await waitFor(() => expect(where()).toBe('/'));
  });

  it('first available schedules page when export is disabled', async () => {
    renderApp('/schedules', {
      ...responses,
      '/api/modules': [{ name: 'export', title: '', enabled: false }, { name: 'loi', title: '', enabled: true }, { name: 'clash', title: '', enabled: true }],
    });

    await waitFor(() => expect(where()).toBe('/loi/schedules'));
  });

  it('target space disabled — goes home', async () => {
    renderApp('/settings/clash', {
      ...responses,
      '/api/modules': [{ name: 'export', title: '', enabled: true }, { name: 'loi', title: '', enabled: true }, { name: 'clash', title: '', enabled: false }],
    });

    await waitFor(() => expect(where()).toBe('/'));
  });
});
