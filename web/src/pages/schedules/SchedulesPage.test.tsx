import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { defaultExportSettings } from '../../components/ExportSettingsForm';
import { renderApp } from '../../test/renderApp';

const schedule = {
  id: 's1', name: 'ЖК «Север» — все', moduleKind: 'export', sourceId: 'src1', sourceName: 'RS 2022', sourceRevitYear: 2022,
  outputFolderTemplate: '\\\\nas\\exports\\{дата}', selection: { models: [{ name: 'АР_К1.rvt', modelPath: 'RSN://rs22/ЖК/АР_К1.rvt', rvt: true, nwc: true }], folders: [] },
  moduleSettings: { presetId: 'p1', overrides: { purge: false } }, recurrenceKind: 'Weekly', daysOfWeekMask: 31, timeOfDay: '20:00:00',
  intervalHours: null, onceAt: null, priority: 0, modelTimeoutMinutes: 30, maxRetries: 1, isEnabled: true, nextRunAt: '2026-10-23T17:00:00Z',
  modelCount: 38, lastRun: { id: 'r1', state: 'Completed', createdAt: '2026-10-22T17:00:00Z', succeeded: 36, failed: 2, total: 38 },
  createdBy: 'А. Борисов', createdAt: '2026-10-02T10:00:00Z', updatedBy: 'И. Петров', updatedAt: '2026-10-21T12:40:00Z', warnings: [],
};

const responses = {
  '/api/schedules': [schedule],
  '/api/sources': [{ id: 'src1', name: 'RS 2022', kind: 'RevitServer', host: 'rs22', revitYear: 2022, rootPath: '' }],
  '/api/export/presets': [{ id: 'p1', name: 'Стандарт', isDefault: true, settings: defaultExportSettings }],
  '/api/schedules/s1/changes': [{ at: '2026-10-21T12:40:00Z', userName: 'И. Петров', summary: 'время 18:00 → 20:00' }],
};

describe('SchedulesPage', () => {
  it('lists schedules with authors and recurrence', async () => {
    renderApp('/schedules', responses);

    expect(await screen.findByText('ЖК «Север» — все')).toBeTruthy();
    expect(screen.getByText('Пн–Пт 20:00')).toBeTruthy();
    expect(screen.getByText('А. Борисов')).toBeTruthy();
    expect(screen.getByText(/изм\. И\. Петров/)).toBeTruthy();
  });

  it('opens the editor of the selected schedule', async () => {
    renderApp('/schedules/s1', responses);

    expect(await screen.findByText('Расписание «ЖК «Север» — все»')).toBeTruthy();
    expect(screen.getByDisplayValue('ЖК «Север» — все')).toBeTruthy();
    expect(screen.getByText(/Создал: А\. Борисов/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Запустить сейчас' })).toBeTruthy();
  });

  it('new schedule form', async () => {
    renderApp('/schedules/new', responses);

    expect(await screen.findByText('Новое расписание')).toBeTruthy();
  });
});

describe('SchedulesPage for a viewer', () => {
  it('hides every editing control', async () => {
    renderApp('/schedules/s1', {
      ...responses,
      '/api/auth/me': { id: 'u3', login: 'gip', displayName: 'Гость ГИП', role: 'Viewer', isDisabled: false, lastLoginAt: null },
    });

    expect(await screen.findByText('Расписание «ЖК «Север» — все»')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '+ Новое расписание' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Запустить сейчас' })).toBeNull();
    expect((screen.getByDisplayValue('ЖК «Север» — все') as HTMLInputElement).disabled).toBe(true);
  });
});
