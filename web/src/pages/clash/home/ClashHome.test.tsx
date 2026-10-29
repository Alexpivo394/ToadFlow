import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const snapshot = (daysAgo: number) => ({
  id: `s${daysAgo}`, extractedAt: new Date(Date.now() - daysAgo * 86_400_000).toISOString(), revitYear: 2022, viewName: 'Navisworks',
  elements: 21430, triangles: 900000, inventory: 25000, geometryBytes: 10_000_000, parametersBytes: 1_000_000, jobId: 'j',
});

const responses = {
  '/api/clash/projects': [
    { object: 'Полковая', name: 'Полковая', parts: 2, models: 3 },
    { object: null, name: 'Без проекта', parts: 0, models: 1 },
  ],
  '/api/clash/parts': [
    {
      id: 'p1', name: 'Корпус 1', object: 'Полковая', part: 'корпус 1', models: 5, sets: 7, updatedAt: '2026-10-28T10:00:00Z',
      groups: ['АР', 'ОВ', 'Проезды'], warnings: ['ЭОМ: нет выгрузки'],
      lastRun: { runId: 'r1', state: 'Succeeded', stage: null, hitCount: 60896, error: null, createdAt: '2026-10-28T10:00:00Z', finishedAt: '2026-10-28T10:01:14Z', stats: null },
      approvedVersion: 2,
      lastApproved: { found: 120, new: 5, active: 98, returned: 1, fixed: 7, goneByRules: 0, unchecked: 0, fake: 16, reusedPairs: 0 },
    },
    { id: 'p2', name: 'Корпус 2', object: 'Полковая', part: 'корпус 2', models: 3, sets: 3, updatedAt: '2026-10-27T10:00:00Z', groups: ['АР'], warnings: [], lastRun: null },
  ],
  '/api/clash/projects/models': [
    { id: 'm1', name: 'POL_K1_AR.rvt', part: 'корпус 1', discipline: 'АР', latest: snapshot(1), snapshotCount: 3, stale: false },
    { id: 'm2', name: 'POL_K1_OV.rvt', part: 'корпус 1', discipline: 'ОВ', latest: snapshot(12), snapshotCount: 1, stale: true },
    { id: 'm3', name: 'POL_K2_AR.rvt', part: 'корпус 2', discipline: 'АР', latest: null, snapshotCount: 0, stale: true },
  ],
  '/api/clash/templates': [{ id: 't1', name: 'Стандарт ЖК', sets: 12, groups: ['АР', 'ОВ'], updatedAt: '2026-10-20T10:00:00Z' }],
  '/api/clash/models': [],
};

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

afterEach(() => {
  cleanup();
  try {
    localStorage.clear();
  } catch {
    // нет хранилища
  }
});

async function pick(label: string, option: string, root: HTMLElement = document.body) {
  fireEvent.click(within(root).getByLabelText(label));
  fireEvent.click(await screen.findByRole('option', { name: option }));
}

describe('Clash home', () => {
  it('ClashHome_ProjectsAndPartCards', async () => {
    renderApp('/clash', responses);

    expect(await screen.findByText('Корпус 1')).toBeTruthy();
    expect(screen.getByText('Полковая')).toBeTruthy();
    expect(screen.getByText('Без проекта')).toBeTruthy();
    expect(screen.getByText('Проезды')).toBeTruthy();
    expect(screen.getByText('ЭОМ: нет выгрузки')).toBeTruthy();
    expect(screen.getByText(/60 896/)).toBeTruthy();
    expect(screen.getByText('не прогонялся')).toBeTruthy();
    expect(screen.getByText(/утверждена v2 · открытых 104/)).toBeTruthy();
    expect(screen.getByText('не утверждена')).toBeTruthy();
    await waitFor(() => expect(calls().some(([url]) => url === `/api/clash/parts?object=${encodeURIComponent('Полковая')}`)).toBe(true));
  });

  it('ClashHome_RemembersProject', async () => {
    renderApp('/clash', responses);
    fireEvent.click(await screen.findByText('Без проекта'));
    await waitFor(() => expect(calls().some(([url]) => url === '/api/clash/parts?object=')).toBe(true));
    cleanup();

    renderApp('/clash', responses);

    await screen.findByText('Без проекта');
    await waitFor(() => expect(calls().filter(([url]) => url.startsWith('/api/clash/parts?')).every(([url]) => url === '/api/clash/parts?object=')).toBe(true));
  });

  it('NewPartWizard_SuggestsModelsAndCreates', async () => {
    renderApp('/clash', responses);
    fireEvent.click(await screen.findByRole('button', { name: '+ Новая часть' }));
    const dialog = await screen.findByRole('dialog');

    await pick('Часть здания', 'корпус 1', dialog);
    expect(await within(dialog).findByText('POL_K1_AR.rvt')).toBeTruthy();
    expect((within(dialog).getByRole('checkbox', { name: 'POL_K1_AR.rvt' }) as HTMLInputElement).checked).toBe(true);
    expect((within(dialog).getByRole('checkbox', { name: 'POL_K1_OV.rvt' }) as HTMLInputElement).checked).toBe(true);
    expect(within(dialog).queryByRole('checkbox', { name: 'POL_K2_AR.rvt' })).toBeNull();
    expect(within(dialog).getByText(/старая/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Далее' }));
    fireEvent.click(await within(dialog).findByLabelText(/По разделам/));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Создать' }));

    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts' && init?.method === 'POST')).toBe(true));
    const [, init] = calls().find(([url, i]) => url === '/api/clash/parts' && i?.method === 'POST')!;
    const body = JSON.parse(String(init!.body));
    expect(body).toMatchObject({ name: 'корпус 1', object: 'Полковая', part: 'корпус 1', start: { kind: 'disciplines' } });
    expect(body.models.map((m: { modelId: string }) => m.modelId)).toEqual(['m1', 'm2']);
  });

  it('NewPartWizard_FromTemplate', async () => {
    renderApp('/clash', responses);
    fireEvent.click(await screen.findByRole('button', { name: '+ Новая часть' }));
    const dialog = await screen.findByRole('dialog');
    await pick('Часть здания', 'корпус 2', dialog);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Далее' }));

    fireEvent.click(await within(dialog).findByLabelText(/Из шаблона/));
    await pick('Шаблон', 'Стандарт ЖК', dialog);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Создать' }));

    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts' && init?.method === 'POST')).toBe(true));
    const [, init] = calls().find(([url, i]) => url === '/api/clash/parts' && i?.method === 'POST')!;
    expect(JSON.parse(String(init!.body)).start).toEqual({ kind: 'template', templateId: 't1' });
  });

  it('UploadsTab_GroupedByProjectWithStale', async () => {
    renderApp('/clash/snapshots', responses);

    expect(await screen.findByText('POL_K2_AR.rvt')).toBeTruthy();
    expect(screen.getByText('Полковая')).toBeTruthy();
    expect(screen.getByText('нет выгрузки')).toBeTruthy();
    expect(screen.getByText(/старая/)).toBeTruthy();
    expect(screen.queryByText('Параметры')).toBeNull(); // каталога на этой странице больше нет
  });

  it('ClashHome_ViewerHasNoNewPart', async () => {
    renderApp('/clash', {
      ...responses,
      '/api/auth/me': { id: 'u2', login: 'viewer', displayName: 'Наблюдатель', role: 'Viewer', isDisabled: false, lastLoginAt: null },
    });

    expect(await screen.findByText('Корпус 1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '+ Новая часть' })).toBeNull();
  });
});
