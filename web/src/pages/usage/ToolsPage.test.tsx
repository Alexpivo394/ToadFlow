import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { pickOption, renderUi } from '../../test/renderApp';
import type { CommandsView } from './api';
import { ToolsPage } from './ToolsPage';

const view = (ribbonOnly: boolean): CommandsView => ({
  basis: { people: 2, machines: 2 },
  ribbonOnly,
  rows: [
    { source: 'ToadTools', commandId: 'Export', name: 'Выгрузка', tab: 'ToadTools', count: 10, people: 2, failed: 1, cancelled: 0, avgMs: 42_000 },
    { source: 'Revit', commandId: 'ID_OBJECTS_WALL', name: 'Стена', tab: 'Архитектура', count: 70, people: 2, failed: 0, cancelled: 0, avgMs: null },
  ],
  unused: [{ commandId: 'Old', name: 'Старая', lastRunAt: '2026-08-01' }],
});

function render(ribbonOnly = true) {
  const requested: string[] = [];
  renderUi(<AuthProvider><ToolsPage /></AuthProvider>, {
    '/api/usage/filters': { people: [], machines: [], disciplines: [], revitYears: [] },
    '/api/usage/commands': (url: string) => {
      requested.push(url);
      return view(ribbonOnly);
    },
  }, '/usage/tools');
  return requested;
}

describe('ToolsPage', () => {
  it('ranks commands with runs, people and failure share for ToadTools only', async () => {
    render();

    expect(await screen.findByText('Выгрузка')).toBeTruthy();
    expect(screen.getByText('Стена')).toBeTruthy();
    expect(screen.getByText('10 %')).toBeTruthy();
    expect(screen.getAllByText('—').length).toBeGreaterThan(0);
  });

  it('marks Revit commands as ribbon-only', async () => {
    render(true);

    expect(await screen.findByText('Штатные команды Revit — только запуски с ленты, без горячих клавиш')).toBeTruthy();
  });

  it('no ribbon-only note when hotkeys are counted', async () => {
    render(false);
    await screen.findByText('Выгрузка');

    expect(screen.queryByText(/только запуски с ленты/)).toBeNull();
  });

  it('lists ToadTools commands not run for 90 days', async () => {
    render();

    expect(await screen.findByText('Не запускали 90 дней')).toBeTruthy();
    expect(screen.getByText('Старая')).toBeTruthy();
  });

  it('source filter goes to the request', async () => {
    const requested = render();
    const select = (await screen.findAllByLabelText('Источник')).find((e) => e.tagName === 'INPUT')!;

    await waitFor(() => pickOption(select, 'Revit'));

    await waitFor(() => expect(requested.some((url) => url.includes('source=Revit'))).toBe(true));
  });
});
