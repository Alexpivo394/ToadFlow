import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { partResponses } from '../../../test/clashPart';
import { navigateTo, renderApp } from '../../../test/renderApp';

vi.mock('../../../api/client', async (original) => ({ ...(await original<typeof import('../../../api/client')>()), downloadFile: vi.fn(async () => {}) }));
const { downloadFile } = await import('../../../api/client');

// WebGL в jsdom нет: вместо сцены three.js — заглушка с номером коллизии.
vi.mock('../viewer/ClashViewer', () => ({ default: (props: { scene: { number: number } }) => <div>сцена {props.scene.number}</div> }));

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const posts = (path: string) => calls().filter(([url, init]) => url === path && init?.method === 'POST');

const row = (n: number, extra: object = {}) => ({
  id: `c${n}`, number: n, status: 'New', assignee: 'Both', foundNow: true, depthMm: 40 + n, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', categoryA: 'OST_Walls', familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k-arov'], firstVersion: 1, lastFoundVersion: 1, suggestedPredecessorId: null, suggestedPredecessorNumber: null, ...extra,
});

const card = (r: ReturnType<typeof row>, feed: object[] = []) => ({ collision: r, feed });

const scene = (url: string) => ({
  runId: 'r1', number: Number(url.split('/collisions/c')[1].split('/')[0]), x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [], meshTriangles: 0, truncated: false,
});

const responses = {
  ...partResponses,
  '/api/clash/parts/p1/versions': [{ version: 2, approvedAt: '2026-10-29T10:00:00Z', userName: 'Иванов', comment: null, runId: null }],
  '/api/clash/parts/p1/approved/run': {
    run: { runId: 'r1', state: 'Succeeded', stage: null, hitCount: 3, error: null, createdAt: '2026-10-29T10:00:00Z', finishedAt: '2026-10-29T10:01:00Z', stats: null },
    version: 2,
    summary: { found: 3, new: 2, active: 1, returned: 0, fixed: 4, goneByRules: 1, unchecked: 0, fake: 1, reusedPairs: 10 },
  },
  '/api/clash/parts/p1/approved/summary': {
    total: 3, statuses: [{ status: 'New', count: 2 }, { status: 'Active', count: 1 }, { status: 'Fake', count: 1 }],
    checks: [{ key: 'k-arov', groupA: 'g-ar', groupAName: 'АР', groupB: 'g-ov', groupBName: 'ОВ', count: 3, toleranceMm: 20 }],
  },
  '/api/clash/parts/p1/approved/collisions': { total: 3, items: [row(1), row(2), row(3, { status: 'Active', suggestedPredecessorId: 'c9', suggestedPredecessorNumber: 9 })] },
  '/api/clash/collisions/c1': card(row(1), [
    { at: '2026-10-29T10:02:00Z', kind: 'Depth', userName: null, text: 'Глубина 41 мм', fromStatus: null, toStatus: null, version: 2, depthMm: 41 },
    { at: '2026-10-29T10:01:00Z', kind: 'Status', userName: 'Иванов', userId: 'u5', text: 'Гильза есть', fromStatus: 'Active', toStatus: 'Fake', version: null, depthMm: null },
    { at: '2026-10-29T10:00:00Z', kind: 'Found', userName: null, text: 'Найдена версией 2, глубина 41 мм', fromStatus: null, toStatus: null, version: 2, depthMm: null },
  ]),
  '/api/clash/collisions/c3': card(row(3, { status: 'Active', suggestedPredecessorId: 'c9', suggestedPredecessorNumber: 9 })),
  '/api/clash/collisions/c1/scene': scene,
  '/api/clash/collisions/c3/scene': scene,
  '/api/clash/collisions/c1/meshes': new ArrayBuffer(4),
  '/api/clash/collisions/c3/meshes': new ArrayBuffer(4),
  'POST /api/clash/collisions/c1/status': null,
  '/api/users/u5/card': { id: 'u5', displayName: 'Иванов', position: 'Инженер КР', email: 'ivanov@example.com', phone: '+7 901 000-00-00', disciplines: ['КР'] },
  'POST /api/clash/collisions/c3/merge': null,
  '/api/clash/parts/p1/approved/facets': { pairs: [{ value: 'АР × ОВ', count: 3 }], levels: [], models: [], categories: [] },
  'POST /api/clash/collisions/c1/seen': null,
  'POST /api/clash/collisions/c3/seen': null,
};

const open = (extra: Record<string, unknown> = {}, path = '/clash/checks/p1') => renderApp(path, { ...responses, ...extra });

describe('Approved tab', () => {
  it('ApprovedTab_DefaultsToOpenStatuses', async () => {
    open();

    expect(await screen.findByRole('button', { name: 'Коллизия 1' })).toBeTruthy();
    const listCall = calls().find(([url]) => url.startsWith('/api/clash/parts/p1/approved/collisions'))!;
    expect(new URL(listCall[0], 'http://x').searchParams.get('status')).toBe('New,Active,Returned,FixedPending,Unchecked');
    expect(screen.getByText('v2')).toBeTruthy();
    expect(screen.getByText(/исправлено 4/)).toBeTruthy();
    expect(within(screen.getByRole('button', { name: 'Коллизия 3' })).getByText('активная')).toBeTruthy();
  });

  it('CollisionCard_FeedShowsDepthAndStatus', async () => {
    open({}, '/clash/checks/p1?collision=c1');

    expect(await screen.findByText('Гильза есть')).toBeTruthy();
    expect(screen.getByText(/активная → фиктивная/)).toBeTruthy();
    expect(screen.getByText('Глубина 41 мм')).toBeTruthy();
    expect(screen.getByText('Найдена версией 2, глубина 41 мм')).toBeTruthy();
    expect(await screen.findByText('сцена 1')).toBeTruthy();
  });

  it('CollisionCard_FakeRequiresNote', async () => {
    open({}, '/clash/checks/p1?collision=c1');
    fireEvent.click(await screen.findByRole('button', { name: 'Фиктивная…' }));

    const apply = screen.getByRole('button', { name: 'Отметить фиктивной' }) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);
    fireEvent.change(screen.getByLabelText('Почему фиктивная'), { target: { value: 'Гильза есть' } });
    expect(apply.disabled).toBe(false);
    fireEvent.click(apply);

    await waitFor(() => expect(posts('/api/clash/collisions/c1/status')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/clash/collisions/c1/status')[0][1]!.body))).toEqual({ status: 'Fake', note: 'Гильза есть' });
  });

  it('CollisionCard_Merge_Confirms', async () => {
    open({}, '/clash/checks/p1?collision=c3');
    expect(await screen.findByText(/Возможно, это коллизия №9/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Объединить' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Да' }));

    await waitFor(() => expect(posts('/api/clash/collisions/c3/merge')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/clash/collisions/c3/merge')[0][1]!.body))).toEqual({ predecessorId: 'c9' });
  });

  it('ApprovedTab_ObserverHasNoActions', async () => {
    open({ '/api/auth/me': { id: 'u2', login: 'viewer', displayName: 'Наблюдатель', role: 'Viewer', isDisabled: false, lastLoginAt: null } },
      '/clash/checks/p1?collision=c3');

    expect(await screen.findByText(/Возможно, это коллизия №9/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Объединить' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Фиктивная…' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Прогнать утверждённую' })).toBeNull();
  });

  /** Набранное примечание и форма «Передать» не переходят на другую коллизию при ↑/↓ или выборе в списке. */
  it('CollisionCard_SwitchCollision_ClearsDraft', async () => {
    open({}, '/clash/checks/p1?collision=c1');
    fireEvent.change(await screen.findByLabelText('Примечание'), { target: { value: 'Двигаем трубу' } });

    fireEvent.click(screen.getByRole('button', { name: 'Коллизия 3' }));

    expect(await screen.findByText(/Возможно, это коллизия №9/)).toBeTruthy();
    expect((screen.getByLabelText('Примечание') as HTMLTextAreaElement).value).toBe('');
  });

  it('Feed_UserNameOpensContactCard', async () => {
    open({}, '/clash/checks/p1?collision=c1');

    fireEvent.click(await screen.findByRole('button', { name: 'Иванов' }));

    expect(await screen.findByText('Инженер КР')).toBeTruthy();
    expect(screen.getByText('ivanov@example.com')).toBeTruthy();
    expect(screen.getByText('+7 901 000-00-00')).toBeTruthy();
  });

  it('ApprovedTab_NotApproved_Explains', async () => {
    open({ '/api/clash/parts/p1/versions': [], '/api/clash/parts/p1/approved/run': null });

    expect(await screen.findByText(/Часть ещё не утверждена/)).toBeTruthy();
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Approved run line', () => {
  it('ApprovedRunLine_ReportAndRunsLinks', async () => {
    const runs = [
      { runId: 'r2', partId: 'p1', version: 2, finishedAt: '2026-10-31T09:05:00Z', trigger: 'schedule', startedBy: 'Ночное', searchSeconds: 10,
        summary: { found: 3, new: 1, active: 2, returned: 0, fixed: 1, goneByRules: 0, unchecked: 0, fake: 0, reusedPairs: 0 } },
      { runId: 'r1', partId: 'p1', version: 1, finishedAt: '2026-10-30T09:05:00Z', trigger: 'manual', startedBy: 'Иванов', searchSeconds: 12,
        summary: { found: 3, new: 3, active: 0, returned: 0, fixed: 0, goneByRules: 0, unchecked: 0, fake: 0, reusedPairs: 0 } },
    ];
    open({ '/api/clash/parts/p1/approved/runs': runs });

    const report = await screen.findByRole('link', { name: 'Отчёт' });
    expect(report.getAttribute('href')).toBe('/clash/reports/r2');
    fireEvent.click(screen.getByRole('button', { name: 'Все прогоны' }));
    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getAllByRole('link').map((l) => l.getAttribute('href'))).toEqual(['/clash/reports/r2', '/clash/reports/r1']);
    expect(within(drawer).getByText(/по расписанию \(Ночное\)/)).toBeTruthy();
  });

  it('ApprovedTab_ExportsListWithItsFilters', async () => {
    const runs = [{ runId: 'r2', partId: 'p1', version: 2, finishedAt: '2026-10-31T09:05:00Z', trigger: 'manual', startedBy: 'Иванов', searchSeconds: 10,
      summary: { found: 3, new: 1, active: 2, returned: 0, fixed: 1, goneByRules: 0, unchecked: 0, fake: 0, reusedPairs: 0 } }];
    open({ '/api/clash/parts/p1/approved/runs': runs }, '/clash/checks/p1?check=k-arov');

    const search = await screen.findByPlaceholderText('№, ID или UniqueId');
    fireEvent.change(search, { target: { value: '1001' } });
    fireEvent.keyDown(search, { key: 'Enter' });
    fireEvent.click(await screen.findByRole('button', { name: 'Excel' }));

    await waitFor(() => expect(downloadFile).toHaveBeenCalled());
    const [path, name] = vi.mocked(downloadFile).mock.calls[0];
    const url = new URL(path, 'http://x');
    expect(url.pathname).toBe('/api/clash/reports/r2/export.xlsx');
    expect(url.searchParams.get('scope')).toBe('open');
    expect(url.searchParams.get('check')).toBe('k-arov');
    expect(url.searchParams.get('q')).toBe('1001');
    expect(url.searchParams.get('status')?.split(',')).toContain('New');
    expect(name).toMatch(/\.xlsx$/);
    expect(screen.getByRole('button', { name: 'HTML' })).toBeTruthy();
  });
});

vi.mock('../../../lib/clipboard', () => ({ copyText: vi.fn(async () => true) }));
const { copyText } = await import('../../../lib/clipboard');

const facets = { pairs: [{ value: 'АР × ОВ', count: 3 }], levels: [{ value: 'Этаж 1', count: 3 }], models: [], categories: [] };

describe('Check page on the shared list', () => {
  const extra = {
    '/api/clash/parts/p1/approved/facets': facets,
    'POST /api/clash/collisions/c1/seen': null,
    'POST /api/clash/collisions/c3/seen': null,
  };

  it('Check_FiltersInUrl_SurviveBackNavigation', async () => {
    open(extra, '/clash/checks/p1?pair=%D0%90%D0%A0%20%C3%97%20%D0%9E%D0%92&status=closed');

    expect(await screen.findByRole('button', { name: 'Коллизия 1' })).toBeTruthy();
    const listCall = calls().map(([url]) => new URL(url, 'http://x')).find((u) => u.pathname === '/api/clash/parts/p1/approved/collisions')!;
    expect([listCall.searchParams.get('pair'), listCall.searchParams.get('status')]).toEqual(['АР × ОВ', 'Fixed,GoneByRules,Fake']);

    fireEvent.click(screen.getByRole('button', { name: 'Коллизия 1' }));
    await screen.findByText('сцена 1');
    navigateTo('/clash/checks/p1?pair=%D0%90%D0%A0%20%C3%97%20%D0%9E%D0%92&status=closed'); // «назад» к списку — фильтр тот же
    await waitFor(() => expect(screen.getAllByLabelText('Статус').filter((e) => e.tagName === 'INPUT').map((e) => (e as HTMLInputElement).value)).toContain('Закрытые'));
  });

  it('Card_OpenMarksSeen', async () => {
    open(extra, '/clash/checks/p1?collision=c1');

    await waitFor(() => expect(posts('/api/clash/collisions/c1/seen')).toHaveLength(1));
  });

  it('Card_ShowInRevit_CopiesTwoLines', async () => {
    open(extra, '/clash/checks/p1?collision=c1');

    fireEvent.click(await screen.findByRole('button', { name: 'Показать в Revit' }));

    await waitFor(() => expect(copyText).toHaveBeenCalled());
    expect(vi.mocked(copyText).mock.calls[0][0]).toBe(`ToadClash №1 — Корпус 1\n${window.location.origin}/clash/checks/p1?collision=c1`);
    expect(await screen.findByText('Номер скопирован. В Revit откройте панель «Коллизии» и нажмите Ctrl+V')).toBeTruthy();
  });

  it('Card_OtherCollisionsOfElement_Link', async () => {
    open({
      ...extra,
      '/api/clash/parts/p1/approved/collisions': (url: string) => {
        const p = new URL(url, 'http://x').searchParams;
        if (p.get('pageSize') === '1') return { total: p.get('element') === 'm1:ua1' ? 4 : 1, items: [] };
        if (p.get('group') === 'element') return { groups: 1, collisions: 4, items: [] };
        return { total: 3, items: [row(1), row(2), row(3)] };
      },
    }, '/clash/checks/p1?collision=c1');

    fireEvent.click(await screen.findByRole('button', { name: /ещё в 3 коллизиях → показать/ }));

    await waitFor(() => {
      const grouped = calls().map(([url]) => new URL(url, 'http://x').searchParams).find((p) => p.get('group') === 'element');
      expect([grouped?.get('element'), grouped?.get('side')]).toEqual(['m1:ua1', 'АР']);
    });
  });

  it('Card_FeedBulkLabel', async () => {
    open({
      ...extra,
      '/api/clash/collisions/c1': card(row(1), [
        { at: '2026-10-29T10:01:00Z', kind: 'Status', userName: 'Иванов', userId: 'u5', text: 'Гильзы есть', fromStatus: 'New', toStatus: 'Fake', version: null, depthMm: null, bulkId: 'b1' },
      ]),
    }, '/clash/checks/p1?collision=c1');

    expect(await screen.findByText('Гильзы есть')).toBeTruthy();
    expect(screen.getByText('массово')).toBeTruthy();
  });
});
