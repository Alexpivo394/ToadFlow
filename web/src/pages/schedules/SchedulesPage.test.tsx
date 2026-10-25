import { fireEvent, screen } from '@testing-library/react';
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

describe('SchedulesPage for LOI', () => {
  const loi = {
    ...schedule, id: 's2', name: 'LOI ЖК «Север»', moduleKind: 'loi', updatedAt: '2026-10-24T12:00:00Z',
    selection: { models: [{ name: 'АР_К1.rvt', modelPath: 'RSN://rs22/ЖК/АР_К1.rvt', rvt: true, nwc: false }], folders: [] },
    moduleSettings: { profileId: 'p1', viewName: null, worksetKeyword: null, skipLinks: true, problemsOnly: false, historyFile: '' },
  };
  const loiResponses = {
    ...responses,
    '/api/schedules': [loi],
    '/api/schedules/s2/changes': [],
    '/api/loi/profiles': [{ id: 'p1', name: 'ОВ', ruleCount: 2, viewName: 'Navisworks', worksetKeyword: '', updatedAt: loi.updatedAt, updatedBy: 'А. Борисов', problems: [] }],
  };

  it('marks LOI schedules and shows the LOI settings tab', async () => {
    renderApp('/schedules/s2', loiResponses);

    expect(await screen.findByText('Расписание «LOI ЖК «Север»»')).toBeTruthy();
    expect(screen.getByText('LOI')).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: 'Проверка LOI' }));
    expect(await screen.findByText('как в профиле: «Navisworks»')).toBeTruthy();
    expect(screen.getByLabelText('База для дашбордов')).toBeTruthy();
  });

  it('by discipline: the project has its own discipline profiles', async () => {
    const byDiscipline = {
      ...loi,
      moduleSettings: { ...loi.moduleSettings, profileId: null, disciplines: [{ discipline: 'ОВ', profileId: 'p1' }], fallbackProfileId: null, modelProfiles: [] },
    };
    renderApp('/schedules/s2', { ...loiResponses, '/api/schedules': [byDiscipline], '/api/loi/disciplines': ['АР', 'ОВ'], '/api/loi/routing/preview': [] });

    fireEvent.click(await screen.findByRole('tab', { name: 'Проверка LOI' }));

    expect(await screen.findByText('Профили разделов проекта')).toBeTruthy();
    expect(screen.getAllByDisplayValue('ОВ').length).toBe(2); // раздел и его профиль
    expect(screen.getByDisplayValue('Пропускать — с пометкой в истории запуска')).toBeTruthy();
    expect(screen.getByRole('button', { name: '+ Раздел' })).toBeTruthy();
  });
});
