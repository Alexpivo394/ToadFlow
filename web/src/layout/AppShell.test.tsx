import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../test/renderApp';

vi.setConfig({ testTimeout: 15_000 });

const user = (role: string, disciplines: string[] = []) => ({
  id: 'u1', login: role.toLowerCase(), displayName: `Пользователь ${role}`, role, isDisabled: false, lastLoginAt: null, disciplines,
});

const modules = (disabled: string[] = []) =>
  ['export', 'loi', 'clash'].map((name) => ({ name, title: name, enabled: !disabled.includes(name) }));

const as = (role: string, path: string, options: { disciplines?: string[]; disabled?: string[] } = {}) =>
  renderApp(path, {
    '/api/auth/me': user(role, options.disciplines),
    '/api/profile': user(role, options.disciplines),
    '/api/modules': modules(options.disabled),
    '/api/overview': { attention: [], tiles: [], server: { version: '3.0.0', startedAt: '2026-11-01T00:00:00Z', databaseBytes: 0, volumes: [], processMemoryBytes: 0, workers: [] }, pulse: { doneDay: 0, failedDay: 0, queued: 0, revitBusy: 0, revitTotal: 0, hourly: [] } },
    '/api/agents': [],
    '/api/settings': { silenceTimeoutMinutes: 10 },
    '/api/clash/checks': [],
    '/api/schedules': [],
    '/api/sources': [],
    '/api/export/presets': [],
  });

const rail = () => screen.findByRole('group', { name: 'Пространства' });
const railNames = async () => within(await rail()).getAllByRole('link').map((link) => link.getAttribute('aria-label'));

describe('AppShell', () => {
  it('rail lists Обзор, enabled modules, Ферма, Администрирование', async () => {
    as('Admin', '/farm/agents');

    await waitFor(async () =>
      expect(await railNames()).toEqual(['Обзор', 'Выгрузка', 'Проверка LOI', 'Коллизии', 'Ферма', 'Администрирование', 'Профиль']));
  });

  it('menu shows only the pages of the current space', async () => {
    as('Admin', '/clash/checks');

    const menu = await screen.findByRole('group', { name: 'Коллизии' });
    expect(within(menu).getByRole('link', { name: 'Проверки' })).toBeTruthy();
    expect(within(menu).getByRole('link', { name: 'Части' })).toBeTruthy();
    expect(screen.queryByRole('link', { name: 'Пресеты' })).toBeNull();
  });

  it('setup pages are under a divider and hidden from a viewer', async () => {
    const admin = as('Admin', '/export/schedules');
    const setup = await screen.findByRole('group', { name: 'Настройка' });
    expect(within(setup).getByRole('link', { name: 'Пресеты' })).toBeTruthy();
    expect(within(setup).queryByRole('link', { name: 'Расписания' })).toBeNull();
    admin.unmount();

    as('Viewer', '/clash/checks');
    await screen.findByRole('group', { name: 'Коллизии' });
    expect(screen.queryByRole('group', { name: 'Настройка' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Части' })).toBeNull();
  });

  it('overview has no space menu', async () => {
    as('Admin', '/');

    await rail();
    expect(screen.queryByRole('group', { name: 'Ферма' })).toBeNull();
    expect(screen.queryByRole('group', { name: 'Коллизии' })).toBeNull();
  });

  it('there is no "Настройки" item anywhere', async () => {
    for (const path of ['/', '/farm/agents', '/clash/setup', '/admin/users']) {
      const view = as('Admin', path);
      await rail();
      expect(screen.queryByText('Настройки')).toBeNull();
      view.unmount();
    }
  });

  it('address of a disabled module redirects to /', async () => {
    as('Admin', '/clash/checks', { disabled: ['clash'] });

    await rail();
    await waitFor(async () => expect(await railNames()).not.toContain('Коллизии'));
    expect(screen.queryByRole('group', { name: 'Коллизии' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Проверки' })).toBeNull();
  });

  it('designer with no available space lands on the profile with a note', async () => {
    as('Designer', '/', { disciplines: ['ОВ'], disabled: ['clash'] });

    expect(await screen.findByText('Для вашей роли пока нет доступных разделов')).toBeTruthy();
  });

  it('clicking a space opens its first visible page', async () => {
    as('Viewer', '/');

    const link = within(await rail()).getByRole('link', { name: 'Администрирование' });
    expect(link.getAttribute('href')).toBe('/admin/modules');
    fireEvent.click(within(await rail()).getByRole('link', { name: 'Ферма' }));
    expect(await screen.findByRole('group', { name: 'Ферма' })).toBeTruthy();
  });
});
