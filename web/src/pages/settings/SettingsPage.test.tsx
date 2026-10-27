import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { defaultExportSettings } from '../../components/ExportSettingsForm';
import { renderApp } from '../../test/renderApp';

const agent = {
  id: 'a1', machineName: 'BIM-FARM-03', ip: '10.0.5.23', agentVersion: '1.0.1', totalRamMb: 32768, state: 'Offline', isDraining: false,
  lastSeenAt: '2026-10-23T07:00:00Z', cpuPercent: 0, ramUsedMb: 0, revits: [{ year: 2020, hasNwcExporter: true }, { year: 2023, hasNwcExporter: false }],
  limits: [{ year: 2020, maxInstances: 1 }, { year: 2023, maxInstances: 1 }], instances: [], isOutdated: true,
};

describe('SettingsPage', () => {
  it('agents: outdated badge and offline agent can be deleted', async () => {
    renderApp('/settings/agents', { '/api/agents': [agent] });

    expect(await screen.findByText('BIM-FARM-03')).toBeTruthy();
    expect(screen.getByText('устарел')).toBeTruthy();
    expect(screen.getByText('R2020: 1 · R2023: 1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Действия' }));
    expect(await screen.findByRole('menuitem', { name: 'Удалить' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '+ Подключить агента' })).toBeTruthy();
  });

  it('sources', async () => {
    renderApp('/settings/sources', { '/api/sources': [{ id: 's', name: 'NAS', kind: 'Folder', host: '\\\\nas\\projects', revitYear: null, rootPath: '' }] });

    expect(await screen.findByText('версия по каждому файлу')).toBeTruthy();
  });

  it('presets', async () => {
    renderApp('/settings/presets', { '/api/export/presets': [{ id: 'p', name: 'Стандарт', isDefault: true, settings: defaultExportSettings }] });

    expect(await screen.findByText('по умолчанию')).toBeTruthy();
  });

  it('users for admin', async () => {
    renderApp('/settings/users', {
      '/api/users': [{ id: 'u2', login: 'petrov', displayName: 'И. Петров', role: 'Operator', isDisabled: false, lastLoginAt: '2026-10-23T08:10:00Z' }],
    });

    expect(await screen.findByText('И. Петров')).toBeTruthy();
  });

  it('general', async () => {
    renderApp('/settings/general', {
      '/api/settings': { timeZoneId: 'Europe/Moscow', defaultModelTimeoutMinutes: 30, silenceTimeoutMinutes: 10, logRetentionDays: 90, recycleAfterModels: 20, recycleMemoryMb: 12000,
        ramReserveMb: 4096, ramPerJobMb: 4096 },
    });

    expect(await screen.findByDisplayValue('90')).toBeTruthy();
  });
});
