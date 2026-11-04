import { screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { partResponses } from './test/clashPart';
import { renderApp } from './test/renderApp';

vi.mock('./pages/clash/viewer/ClashViewer', () => ({ default: () => <div>сцена</div> }));

const user = (role: string, disciplines: string[] = []) => ({
  id: 'u1', login: role.toLowerCase(), displayName: `Пользователь ${role}`, role, isDisabled: false, lastLoginAt: null, isPending: false,
  position: null, email: null, phone: null, disciplines, createdAt: '2026-10-01T00:00:00Z',
});

const check = {
  partId: 'p1', name: 'Корпус 1', object: 'Полковая', part: 'корпус 1', version: 2, lastRunAt: '2026-10-30T10:00:00Z',
  lastRun: { found: 3, new: 1, active: 2, returned: 0, fixed: 0, goneByRules: 0, unchecked: 0, fake: 0, reusedPairs: 0 }, open: 3,
  models: [{ modelId: 'm1', name: 'POL_K1_AR.rvt', discipline: 'АР' }, { modelId: 'm2', name: 'POL_K1_OV.rvt', discipline: 'ОВ' }],
};

const row = (n: number) => ({
  id: `c${n}`, number: n, status: 'New', assignee: 'Both', foundNow: true, depthMm: 40, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', categoryA: 'OST_Walls', familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k'], firstVersion: 1, lastFoundVersion: 2, suggestedPredecessorId: n === 3 ? 'c9' : null, suggestedPredecessorNumber: n === 3 ? 9 : null,
});

const scene = { runId: 'r1', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [], meshTriangles: 0, truncated: false };

const responses = {
  ...partResponses,
  '/api/dashboard': { kpi: { exported: 0, failed: 0, failedPercent: 0, queued: 0, running: 0, avgSeconds: null, agentsOnline: 0, agentsTotal: 0 }, days: [], upcoming: [], recentErrors: [] },
  '/api/agents': [],
  '/api/settings': { silenceTimeoutMinutes: 10 },
  '/api/clash/overview': { notUploaded: [], runFailed: [], noRecentRun: [], unchecked: [], spike: [], stuck: [], noSchedule: [], agentsOffline: null },
  '/api/clash/checks': [check],
  '/api/clash/checks/p1': check,
  '/api/clash/my': { total: 0, items: [] },
  '/api/clash/my/summary': { disciplines: ['ОВ'], toFix: 0, takenByOther: 0, newSinceVisit: 0, previousSeenAt: null },
  'POST /api/clash/my/visit': null,
  '/api/clash/parts/p1/versions': [{ version: 2, approvedAt: '2026-10-29T10:00:00Z', userName: 'Иванов', comment: null, runId: null }],
  '/api/clash/parts/p1/approved/run': null,
  '/api/clash/parts/p1/approved/summary': { total: 2, statuses: [{ status: 'New', count: 2 }], checks: [] },
  '/api/clash/parts/p1/approved/collisions': { total: 2, items: [row(1), row(3)] },
  '/api/clash/collisions/c1': { collision: row(1), feed: [] },
  '/api/clash/collisions/c3': { collision: row(3), feed: [] },
  '/api/clash/collisions/c1/scene': scene,
  '/api/clash/collisions/c3/scene': scene,
  '/api/clash/collisions/c1/meshes': new ArrayBuffer(4),
  '/api/clash/collisions/c3/meshes': new ArrayBuffer(4),
};

const as = (role: string, path: string, disciplines: string[] = []) => renderApp(path, { ...responses, '/api/auth/me': user(role, disciplines) });

const nav = () => screen.getByRole('navigation');

describe('Role workspaces', () => {
  it('Home_StaffSeeDashboard_DesignerGoesToMy', async () => {
    for (const role of ['Viewer', 'Operator', 'Admin']) {
      const { unmount } = as(role, '/');
      expect(await screen.findByRole('heading', { name: 'Обзор' })).toBeTruthy();
      unmount();
    }

    const designer = as('Designer', '/', ['ОВ']);
    expect(await screen.findByRole('heading', { name: 'Мои коллизии' })).toBeTruthy();
    designer.unmount();

    as('Designer', '/');
    expect(await screen.findByRole('heading', { name: 'Проверки' })).toBeTruthy();
  });

  it('Nav_DesignerSeesOnlyClash', async () => {
    as('Designer', '/clash/checks', ['ОВ']);
    await screen.findByRole('heading', { name: 'Проверки' });

    const menu = within(nav());
    expect(menu.getByText('Мои')).toBeTruthy();
    expect(menu.getByText('Сводка')).toBeTruthy();
    for (const hidden of ['Обзор', 'Расписания', 'История и логи', 'Аналитика LOI', 'Настройки', 'Настройка проверок'])
      expect(menu.queryByText(hidden)).toBeNull();
  });

  it('Nav_OperatorSeesSetup', async () => {
    as('Operator', '/clash/checks');
    await screen.findByRole('heading', { name: 'Проверки' });

    const menu = within(nav());
    expect(menu.getByText('Настройка проверок')).toBeTruthy();
    expect(menu.getByText('Части')).toBeTruthy();
    expect(menu.getByText('Шаблоны')).toBeTruthy();
    expect(menu.getByText('Выгрузки')).toBeTruthy();
    expect(menu.queryByText('Мои')).toBeNull(); // разделов нет
  });

  it('Redirect_OldApprovedLink_OpensCheckWithCollision', async () => {
    as('Admin', '/clash/parts/p1?tab=approved&collision=c3');

    expect(await screen.findByText(/Возможно, это коллизия №9/)).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Корпус 1' })).toBeTruthy();
  });

  it('Redirect_OldPartLink_OpensSetupPart', async () => {
    as('Admin', '/clash/parts/p1?tab=sets');

    expect(await screen.findByLabelText('Имя набора')).toBeTruthy();
  });

  it('CheckPage_DesignerCanSetStatus_NoRunButtons', async () => {
    as('Designer', '/clash/checks/p1?collision=c3', ['ОВ']);

    expect(await screen.findByRole('button', { name: 'Фиктивная…' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Прогнать утверждённую' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Объединить' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Настроить' })).toBeNull();
  });

  it('CheckPage_OperatorHasSetupLink', async () => {
    as('Operator', '/clash/checks/p1');

    const link = await screen.findByRole('link', { name: 'Настроить' });
    expect(link.getAttribute('href')).toBe('/clash/setup/parts/p1');
    expect(screen.getByRole('button', { name: 'Прогнать утверждённую' })).toBeTruthy();
  });

  it('PartShell_HasLinkToCheck_NoApprovedTab', async () => {
    as('Operator', '/clash/setup/parts/p1?tab=sets');

    await screen.findByLabelText('Имя набора');
    expect(screen.queryByRole('tab', { name: 'Утверждённая' })).toBeNull();
    await waitFor(() => expect(screen.getByRole('link', { name: 'Коллизии этой проверки' }).getAttribute('href')).toBe('/clash/checks/p1'));
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Menu names', () => {
  it('Menu_OverviewOfChecks', async () => {
    as('Admin', '/settings/clash');
    await waitFor(() => expect(within(nav()).getByText('Хранение и пороги коллизий')).toBeTruthy());

    const menu = within(nav());
    expect(menu.getByText('Обзор проверок')).toBeTruthy();
    expect(menu.getByText('Хранение и пороги коллизий')).toBeTruthy();
  });
});
