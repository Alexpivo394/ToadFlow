import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { partResponses } from '../../../test/clashPart';
import { renderApp } from '../../../test/renderApp';

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
