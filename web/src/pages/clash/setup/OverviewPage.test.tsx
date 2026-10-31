import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const empty = { notUploaded: [], runFailed: [], noRecentRun: [], unchecked: [], spike: [], stuck: [] };
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

    expect(await screen.findByText('Всё в порядке')).toBeTruthy();
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
