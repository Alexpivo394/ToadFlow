import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const empty = { notUploaded: [], runFailed: [], noRecentRun: [], unchecked: [], spike: [], stuck: [], noSchedule: [], agentsOffline: null, checks: 3 };
const settings = { storage: { keepSnapshots: 3 }, overview: { staleDays: 7, noRunDays: 7, spikeShare: 0.1, spikeMin: 50, stuckDays: 30 } };

const problems = {
  ...empty,
  notUploaded: [{ partId: 'p1', partName: 'Корпус 1', modelId: 'm2', modelName: 'POL_K1_OV.rvt', reason: 'выгрузка старше 7 дней' },
    { partId: 'p1', partName: 'Корпус 1', modelId: 'm3', modelName: 'POL_K1_VK.rvt', reason: 'нет выгрузки' }],
  runFailed: [{ partId: 'p2', name: 'Корпус 2', object: 'Полковая', count: 0, reason: 'прогон упал: нет памяти' }],
  stuck: [{ partId: 'p1', name: 'Корпус 1', object: 'Полковая', count: 340, reason: 'висят 340' }],
};

describe('Coordinator overview', () => {
  it('Overview_EmptyShowsAllGood', async () => {
    renderApp('/clash/setup', { '/api/clash/overview': empty, '/api/clash/settings': settings });

    expect(await screen.findByText('Всё в порядке: 3 проверки, все прогнаны вовремя, агенты на связи')).toBeTruthy();
  });

  it('Overview_ItemLinksToCheckWithFilter', async () => {
    renderApp('/clash/setup', { '/api/clash/overview': problems, '/api/clash/settings': settings });

    const stuck = await screen.findByRole('link', { name: /Корпус 1 — висят 340/ });
    expect(stuck.getAttribute('href')).toBe('/clash/checks/p1?stuckDays=30');
    expect(screen.getByRole('link', { name: /POL_K1_OV\.rvt — выгрузка старше 7 дней/ }).getAttribute('href')).toBe('/clash/setup/parts/p1?tab=models');
    expect(screen.getByRole('link', { name: /Корпус 2 — прогон упал: нет памяти/ }).getAttribute('href')).toBe('/clash/checks/p2');
    expect(screen.queryByText(/заявк/)).toBeNull(); // регистрация — не часть коллизий
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Coordinator overview: schedules and agents', () => {
  it('Overview_NoScheduleAndAgentsOffline_Shown', async () => {
    renderApp('/clash/setup', {
      '/api/clash/overview': {
        ...empty,
        noSchedule: [{ partId: 'p3', name: 'Корпус 3', object: 'Полковая', count: 0, reason: 'нет регулярного прогона' }],
        agentsOffline: { minutes: 25, reason: 'Агенты не в сети 25 мин' },
      },
      '/api/clash/settings': settings,
    });

    expect(await screen.findByText('Агенты не в сети 25 мин')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Агенты' }).getAttribute('href')).toBe('/settings/agents');
    expect(screen.getByText('Нет регулярного прогона')).toBeTruthy();
    expect(screen.getByRole('link', { name: /Корпус 3 — нет регулярного прогона/ }).getAttribute('href')).toBe('/clash/checks/p3');
    expect(screen.queryByText(/Всё в порядке/)).toBeNull();
  });

  it('Overview_EngineSilent_Warning', async () => {
    renderApp('/clash/setup', {
      '/api/clash/overview': empty,
      '/api/clash/settings': settings,
      '/api/clash/engine/status': { lastSeenAt: null, serverStartedAt: new Date(Date.now() - 30 * 60_000).toISOString(), queued: 2 },
    });

    expect(await screen.findByText('Движок не отвечает 30 мин')).toBeTruthy();
    expect(screen.queryByText(/Всё в порядке/)).toBeNull();
  });
});
