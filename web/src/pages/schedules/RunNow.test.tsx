import type { ReactElement } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { describe, expect, it, vi } from 'vitest';
import { defaultExportSettings } from '../../components/ExportSettingsForm';
import { renderApp } from '../../test/renderApp';

const schedule = {
  id: 's1', name: 'ЖК «Север» — все', moduleKind: 'export', sourceId: 'src1', sourceName: 'RS 2022', sourceRevitYear: 2022,
  outputFolderTemplate: '\\nas\exports\{дата}', selection: { models: [{ name: 'АР_К1.rvt', modelPath: 'RSN://rs22/ЖК/АР_К1.rvt', rvt: true, nwc: true }], folders: [] },
  moduleSettings: { presetId: 'p1', overrides: { purge: false } }, recurrenceKind: 'Weekly', daysOfWeekMask: 31, timeOfDay: '20:00:00',
  intervalHours: null, onceAt: null, priority: 0, modelTimeoutMinutes: 30, maxRetries: 1, isEnabled: true, nextRunAt: '2026-10-23T17:00:00Z',
  modelCount: 38, lastRun: null, createdBy: 'А. Борисов', createdAt: '2026-10-02T10:00:00Z', updatedBy: null, updatedAt: '2026-10-21T12:40:00Z', warnings: [],
};

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

describe('Run now', () => {
  it('RunNow_ToastLinksToRun', async () => {
    const show = vi.spyOn(notifications, 'show');
    renderApp('/schedules/s1', {
      '/api/schedules': [schedule],
      '/api/sources': [{ id: 'src1', name: 'RS 2022', kind: 'RevitServer', host: 'rs22', revitYear: 2022, rootPath: '' }],
      '/api/export/presets': [{ id: 'p1', name: 'Стандарт', isDefault: true, settings: defaultExportSettings }],
      '/api/schedules/s1/changes': [],
      'POST /api/schedules/s1/run': { runId: 'r9' },
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Запустить сейчас' }));

    await waitFor(() => expect(show).toHaveBeenCalledWith(expect.objectContaining({ title: 'Запуск создан' })));
    const toast = show.mock.calls.find(([n]) => n.title === 'Запуск создан')![0];
    render(<MantineProvider>{toast.message as ReactElement}</MantineProvider>);
    fireEvent.click(screen.getByRole('button', { name: 'Открыть запуск' }));
    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/runs/r9'))).toBe(true));
    show.mockRestore();
  });
});
