import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider, useAuth } from '../auth/AuthContext';
import { renderUi } from '../test/renderApp';
import { useSpaces, visiblePages } from './useSpaces';

const user = (role: string, disciplines: string[] = []) => ({
  id: 'u1', login: role.toLowerCase(), displayName: role, role, isDisabled: false, lastLoginAt: null, disciplines,
});

const modules = (disabled: string[] = []) =>
  ['export', 'loi', 'clash'].map((name) => ({ name, title: name, enabled: !disabled.includes(name) }));

/** Пространства и их страницы строкой «id: адрес,адрес». */
function Probe() {
  const { spaces, loading } = useSpaces();
  const auth = useAuth();
  if (loading || !auth.user) return <div>загрузка</div>;
  return (
    <ul>
      {spaces.map((space) => (
        <li key={space.id}>{`${space.id}: ${visiblePages(space, auth).map((p) => p.to).join(',')}`}</li>
      ))}
    </ul>
  );
}

async function spacesOf(role: string, options: { disciplines?: string[]; disabled?: string[] } = {}) {
  renderUi(<AuthProvider><Probe /></AuthProvider>, {
    '/api/auth/me': user(role, options.disciplines),
    '/api/modules': modules(options.disabled),
  });
  const items = await screen.findAllByRole('listitem');
  return Object.fromEntries(items.map((item) => item.textContent!.split(': ')).map(([id, pages]) => [id, pages.split(',')]));
}

describe('useSpaces', () => {
  it('administrator with all modules sees five spaces in order', async () => {
    const spaces = await spacesOf('Admin', { disciplines: ['ОВ'] });

    expect(Object.keys(spaces)).toEqual(['export', 'loi', 'clash', 'farm', 'admin']);
    expect(spaces.export).toEqual(['/export/schedules', '/export/history', '/export/presets']);
    expect(spaces.clash).toContain('/clash/setup/settings');
    expect(spaces.admin).toEqual(['/admin/users', '/admin/modules', '/admin/server', '/admin/events']);
  });

  it('families space appears when the module is enabled and is visible to a designer', async () => {
    const withFamilies = [...modules(), { name: 'families', title: 'Семейства', enabled: true }];
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': user('Designer'), '/api/modules': withFamilies });
    const items = await screen.findAllByRole('listitem');

    expect(items.map((item) => item.textContent)).toContain('families: /families/catalog,/families/requests');
  });

  it('families pages by role: coordinator reviews and decides, viewer only looks', async () => {
    const withFamilies = [...modules(), { name: 'families', title: 'Семейства', enabled: true }];
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': user('Operator'), '/api/modules': withFamilies });

    const items = (await screen.findAllByRole('listitem')).map((item) => item.textContent);

    expect(items).toContain('families: /families/catalog,/families/requests,/families/review,/families/issues,/families/misses,/families/analytics,/families/settings');
  });

  it('families space is absent while the module is not enabled', async () => {
    const spaces = await spacesOf('Admin');

    expect(Object.keys(spaces)).not.toContain('families');
  });

  it('a disabled module has no space', async () => {
    const spaces = await spacesOf('Admin', { disabled: ['clash'] });

    expect(Object.keys(spaces)).toEqual(['export', 'loi', 'farm', 'admin']);
  });

  it('designer sees only spaces with designer pages', async () => {
    const spaces = await spacesOf('Designer', { disciplines: ['ОВ'] });

    expect(spaces).toEqual({ loi: ['/loi/analytics'], clash: ['/clash/my', '/clash/checks', '/clash/summary'] });
  });

  it('designer without disciplines does not see "Мои"', async () => {
    const spaces = await spacesOf('Designer');

    expect(spaces).toEqual({ loi: ['/loi/analytics'], clash: ['/clash/checks', '/clash/summary'] });
  });

  it('viewer sees admin without "Пользователи"', async () => {
    const spaces = await spacesOf('Viewer');

    expect(spaces.admin).toEqual(['/admin/modules', '/admin/server', '/admin/events']);
    expect(spaces.clash).toEqual(['/clash/checks', '/clash/summary']); // настройка коллизий — с координатора
  });

  it('operator does not see setup pages of admin role', async () => {
    const spaces = await spacesOf('Operator');

    expect(spaces.clash).toEqual(['/clash/checks', '/clash/summary', '/clash/setup', '/clash/setup/parts', '/clash/setup/templates',
      '/clash/setup/snapshots', '/clash/setup/schedules', '/clash/setup/history', '/clash/setup/settings']);
    expect(spaces.admin).toContain('/admin/users');
  });

  it('no space at all for a designer with clash and LOI disabled', async () => {
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': user('Designer', ['ОВ']), '/api/modules': modules(['clash', 'loi']) });

    expect(await screen.findByRole('list')).toBeTruthy();
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
  });
});

describe('usage space', () => {
  const withUsage = (enabled = true) => [...modules(), { name: 'usage', title: 'Аналитика', enabled }];

  it('viewer sees analytics with all its screens and settings', async () => {
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': user('Viewer'), '/api/modules': withUsage() });
    const items = (await screen.findAllByRole('listitem')).map((item) => item.textContent);

    expect(items).toContain('usage: /usage,/usage/waits,/usage/tools,/usage/machines,/usage/service,/usage/coverage,/usage/settings');
  });

  it('designer does not see analytics', async () => {
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': user('Designer', ['ОВ']), '/api/modules': withUsage() });
    const items = (await screen.findAllByRole('listitem')).map((item) => item.textContent!.split(':')[0]);

    expect(items).not.toContain('usage');
  });

  it('analytics is absent while the module is disabled', async () => {
    renderUi(<AuthProvider><Probe /></AuthProvider>, { '/api/auth/me': user('Admin'), '/api/modules': withUsage(false) });
    const items = (await screen.findAllByRole('listitem')).map((item) => item.textContent!.split(':')[0]);

    expect(items).not.toContain('usage');
  });
});
