import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { pickOption, renderApp } from '../../test/renderApp';

const server = {
  version: '3.0.0', startedAt: '2026-11-10T10:00:00Z', databaseBytes: 4.2 * 1024 ** 3,
  volumes: [{ name: 'Сервер', freeBytes: 61 * 1024 ** 3, totalBytes: 200 * 1024 ** 3 }], processMemoryBytes: 900 * 1024 ** 2,
  workers: [{ module: 'clash', title: 'Движок коллизий', lastSeenAt: null, online: false }],
};

const modules = [
  { name: 'export', title: 'Выгрузка', enabled: true },
  { name: 'loi', title: 'Проверка LOI', enabled: false },
  { name: 'clash', title: 'Коллизии', enabled: true },
];

const general = {
  timeZoneId: 'Europe/Moscow', defaultModelTimeoutMinutes: 30, silenceTimeoutMinutes: 10, logRetentionDays: 90, recycleAfterModels: 20,
  recycleMemoryMb: 12000, ramReserveMb: 4096, ramPerJobMb: 4096, agentsOfflineMinutes: 10,
};

const viewer = { id: 'u2', login: 'boss', displayName: 'Руководитель', role: 'Viewer', isDisabled: false, lastLoginAt: null };

describe('Administration pages', () => {
  it('modules page lists enabled and disabled modules and has no switches', async () => {
    renderApp('/admin/modules', { '/api/modules': modules, '/api/server': server });

    const main = await screen.findByRole('main');
    await waitFor(() => expect(within(main).getByText('Движок коллизий: не на связи')).toBeTruthy());
    const rows = within(main).getAllByRole('row');
    expect(rows.slice(1).map((row) => within(row).getAllByRole('cell').map((cell) => cell.textContent))).toEqual([
      ['Выгрузка', 'включён', '—'],
      ['Проверка LOI', 'выключен', '—'],
      ['Коллизии', 'включён', 'Движок коллизий: не на связи'],
    ]);
    expect(within(main).queryByRole('switch')).toBeNull();
    expect(within(main).queryByRole('checkbox')).toBeNull();
    expect(within(main).getByText(/MODULES в файле \.env/)).toBeTruthy();
  });

  it('server page shows stats and the settings form is read-only for a viewer', async () => {
    renderApp('/admin/server', { '/api/auth/me': viewer, '/api/server': server, '/api/settings': general });

    expect(await screen.findByText(/Версия 3\.0\.0/)).toBeTruthy();
    expect(screen.getByText(/Сервер: свободно 61 ГБ/)).toBeTruthy();
    expect(((await screen.findByLabelText('Хранить логи, дней')) as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('events page sends actor, kind and period to the server', async () => {
    const urls: string[] = [];
    renderApp('/admin/events', {
      '/api/events': (url: string) => {
        urls.push(url);
        return [{ id: 7, at: '2026-11-20T10:00:00Z', actorKind: 'User', actorName: 'Иван Петров', kind: 'ScheduleChanged', text: 'Изменено расписание' }];
      },
      '/api/events/kinds': ['ScheduleChanged', 'UserChanged'],
    });

    expect(await screen.findByText('Изменено расписание')).toBeTruthy();
    fireEvent.change(screen.getAllByLabelText('Кто').find((e) => e.tagName === 'INPUT')!, { target: { value: 'иван' } });
    await pickOption(screen.getAllByLabelText('Вид').find((e) => e.tagName === 'INPUT')!, 'UserChanged');
    await pickOption(screen.getAllByLabelText('Период').find((e) => e.tagName === 'INPUT')!, 'За сутки');

    await waitFor(() => {
      const last = new URL(urls[urls.length - 1], 'http://x');
      expect(last.searchParams.get('actor')).toBe('иван');
      expect(last.searchParams.get('kind')).toBe('UserChanged');
      expect(last.searchParams.get('from')).toBeTruthy();
    });
  });

  it('history no longer has the system events tab', async () => {
    renderApp('/farm/queue', { '/api/runs': { items: [], total: 0 }, '/api/schedules': [], '/api/agents': [], '/api/farm/summary': null });

    await screen.findByRole('tab', { name: 'Запуски' });
    expect(screen.queryByRole('tab', { name: 'События системы' })).toBeNull();
  });
});
