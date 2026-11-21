import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { pickOption, renderUi } from '../../test/renderApp';
import type { FamiliesSettings, LibraryAgent, LibraryStatus } from './api';
import { SettingsPage } from './SettingsPage';

const user = (role: string) => ({ id: 'u1', login: role.toLowerCase(), displayName: role, role, isDisabled: false, lastLoginAt: null, disciplines: [] });

const root = '\\\\nas\\Families';
const settings: FamiliesSettings = { root, reviewFolder: '_На проверке', scanIntervalMinutes: 10, libraryAgentId: null };
const status: LibraryStatus = {
  configured: true, agentName: 'BIM-LIB-01', agentOnline: true, lastScanAt: '2026-11-20T10:00:00Z', lastScanComplete: false,
  lastScanErrors: ['КР\\Архив: Отказано в доступе'], pendingReads: 3, failedReads: 1,
};
const agents: LibraryAgent[] = [{ id: 'a1', machineName: 'BIM-LIB-01', online: true }, { id: 'a2', machineName: 'BIM-FARM-02', online: false }];

function render(role: string) {
  const posted: string[] = [];
  const saved: unknown[] = [];
  renderUi(<AuthProvider><SettingsPage /></AuthProvider>, {
    '/api/auth/me': user(role),
    '/api/families/settings': settings,
    '/api/families/status': status,
    '/api/families/agents': agents,
    'POST /api/families/scan': () => {
      posted.push('scan');
      return { scanId: 's1' };
    },
    'PUT /api/families/settings': (_url: string, init?: RequestInit) => {
      saved.push(JSON.parse(String(init?.body)));
      return settings;
    },
  });
  return { posted, saved };
}

const input = (label: string) => screen.getAllByLabelText(label).find((e) => e.tagName === 'INPUT')!;

describe('SettingsPage', () => {
  it('settings are read-only for an operator, editable for an administrator', async () => {
    render('Operator');

    expect(await screen.findByDisplayValue(root)).toHaveProperty('disabled', true);
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('administrator saves the library settings', async () => {
    const { saved } = render('Admin');

    fireEvent.change(await screen.findByDisplayValue(root), { target: { value: '  Y:\\12-BIM\\Семейства ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(saved).toEqual([{ ...settings, root: 'Y:\\12-BIM\\Семейства' }]));
  });

  it('a wrong root is shown before saving', async () => {
    render('Admin');

    fireEvent.change(await screen.findByDisplayValue(root), { target: { value: 'Families' } });

    expect(screen.getByText(/Нужен путь UNC/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true);
  });

  it('status block shows the last scan and its errors', async () => {
    render('Viewer');

    expect(await screen.findByText('КР\\Архив: Отказано в доступе')).toBeTruthy();
    expect(screen.getByText(/неполный/)).toBeTruthy();
    expect(screen.getAllByText('BIM-LIB-01').length).toBeGreaterThan(0);
    expect(screen.getByText('3')).toBeTruthy();
  });

  it('"Обойти сейчас" posts a scan request and is hidden from a viewer', async () => {
    const { posted } = render('Operator');

    fireEvent.click(await screen.findByRole('button', { name: 'Обойти сейчас' }));

    await waitFor(() => expect(posted).toEqual(['scan']));
  });

  it('viewer has no scan button', async () => {
    render('Viewer');

    await screen.findByText('КР\\Архив: Отказано в доступе');
    expect(screen.queryByRole('button', { name: 'Обойти сейчас' })).toBeNull();
  });

  it('agent selector lists only agents that can scan', async () => {
    const { saved } = render('Admin');
    await screen.findByDisplayValue(root);

    await pickOption(input('Агент библиотеки'), 'BIM-FARM-02 — не в сети');
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(saved).toEqual([{ ...settings, libraryAgentId: 'a2' }]));
  });
});
