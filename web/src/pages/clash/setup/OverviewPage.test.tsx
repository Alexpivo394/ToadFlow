import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const empty = { notUploaded: [], runFailed: [], noRecentRun: [], unchecked: [], spike: [], stuck: [], pendingUsers: 0 };
const settings = { storage: { keepSnapshots: 3 }, overview: { staleDays: 7, noRunDays: 7, spikeShare: 0.1, spikeMin: 50, stuckDays: 30 } };

const problems = {
  ...empty,
  notUploaded: [{ partId: 'p1', partName: 'Корпус 1', modelId: 'm2', modelName: 'POL_K1_OV.rvt', reason: 'выгрузка старше 7 дней' },
    { partId: 'p1', partName: 'Корпус 1', modelId: 'm3', modelName: 'POL_K1_VK.rvt', reason: 'нет выгрузки' }],
  runFailed: [{ partId: 'p2', name: 'Корпус 2', object: 'Полковая', count: 0, reason: 'прогон упал: нет памяти' }],
  stuck: [{ partId: 'p1', name: 'Корпус 1', object: 'Полковая', count: 340, reason: 'висят 340' }],
  pendingUsers: 3,
};

const dashboard = { kpi: { exported24h: 0, failed24h: 0, failedPercent: 0, queued: 0, running: 0, avgDurationSeconds24h: null, agentsOnline: 0, agentsTotal: 0 }, daily: [], upcoming: [], recentErrors: [] };
const viewer = { id: 'u2', login: 'boss', displayName: 'Руководитель', role: 'Viewer', isDisabled: false, lastLoginAt: null };

describe('Coordinator overview', () => {
  it('Overview_EmptyShowsAllGood', async () => {
    renderApp('/clash/setup', { '/api/clash/overview': empty, '/api/clash/settings': settings });

    expect(await screen.findByText('Всё в порядке')).toBeTruthy();
  });

  it('Overview_ItemLinksToCheckWithFilter', async () => {
    renderApp('/clash/setup', { '/api/clash/overview': problems, '/api/clash/settings': settings });

    const stuck = await screen.findByRole('link', { name: /Корпус 1 — висят 340/ });
    expect(stuck.getAttribute('href')).toBe('/clash/checks/p1?stuckDays=30');
    expect(screen.getByRole('link', { name: /POL_K1_OV\.rvt — выгрузка старше 7 дней/ }).getAttribute('href')).toBe('/clash/setup/parts/p1?tab=models');
    expect(screen.getByRole('link', { name: /Корпус 2 — прогон упал: нет памяти/ }).getAttribute('href')).toBe('/clash/checks/p2');
    expect(screen.getByRole('link', { name: /3 заявки на регистрацию/ }).getAttribute('href')).toBe('/settings/users');
  });

  it('Dashboard_ClashAttention_CountsLinkToOverview', async () => {
    const base = { '/api/dashboard': dashboard, '/api/agents': [], '/api/settings': { silenceTimeoutMinutes: 10 }, '/api/clash/settings': settings };
    const { unmount } = renderApp('/', { ...base, '/api/clash/overview': problems });

    expect(await screen.findByText(/2 модели не выгрузились/)).toBeTruthy();
    expect(screen.getByText(/1 прогон не прошёл/)).toBeTruthy();
    expect(screen.getByText(/3 заявки на регистрацию/)).toBeTruthy();
    fireEvent.click(screen.getByRole('link', { name: /2 модели не выгрузились/ }));
    expect(await screen.findByRole('heading', { name: 'Обзор проверок' })).toBeTruthy();
    unmount();

    const calm = renderApp('/', { ...base, '/api/clash/overview': empty });
    expect(await screen.findByText('Коллизии: всё в порядке')).toBeTruthy();
    calm.unmount();

    renderApp('/', { ...base, '/api/clash/overview': problems, '/api/auth/me': viewer });
    await screen.findByRole('heading', { name: 'Обзор' });
    expect(screen.queryByText(/Коллизии:/)).toBeNull();
  });
});

vi.setConfig({ testTimeout: 15_000 });
