import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../../test/renderApp';

vi.mock('../viewer/ClashViewer', () => ({ default: () => <div>сцена</div> }));

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const posts = (path: string) => calls().filter(([url, init]) => url === path && init?.method === 'POST');
const myCalls = () => calls().filter(([url]) => url.startsWith('/api/clash/my?')).map(([url]) => new URL(url, 'http://x').searchParams);

const designer = (disciplines: string[]) => ({
  id: 'u1', login: 'ovk', displayName: 'Ольга', role: 'Designer', isDisabled: false, lastLoginAt: null, isPending: false,
  position: null, email: null, phone: null, disciplines, createdAt: '2026-10-01T00:00:00Z',
});

const collision = (n: number, assignee = 'Both') => ({
  id: `c${n}`, number: n, status: 'New', assignee, foundNow: true, depthMm: 40, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'DON_K1_KR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'КР', categoryA: 'OST_Walls', familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'DON_K1_OVK.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВК', categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k'], firstVersion: 1, lastFoundVersion: 1, suggestedPredecessorId: null, suggestedPredecessorNumber: null,
});

const row = (n: number, assignee = 'Both') => ({
  collision: collision(n, assignee), partId: 'p1', partName: 'Корпус 1', object: 'DON', part: 'корпус 1',
  mineToFix: assignee !== 'SideA', takenBy: assignee === 'SideA' ? 'Пётр' : null, takenAt: assignee === 'SideA' ? '2026-10-30T10:00:00Z' : null,
});

const scene = { runId: 'r1', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [], meshTriangles: 0, truncated: false };

function responses(disciplines = ['ОВК']) {
  let taken = false; // «Беру» переносит №2 в «Мне править»
  return {
    '/api/auth/me': designer(disciplines),
    '/api/clash/checks': [{ partId: 'p1', name: 'Корпус 1', object: 'DON', part: 'корпус 1', version: 1, lastRunAt: null, lastRun: null, open: 2, models: [] }],
    '/api/clash/my/summary': { disciplines, toFix: taken ? 2 : 1, takenByOther: taken ? 0 : 1, newSinceVisit: 1, previousSeenAt: null },
    'POST /api/clash/my/visit': null,
    '/api/clash/my': (url: string) => {
      const scope = new URL(url, 'http://x').searchParams.get('scope');
      if (disciplines.length === 0) return { total: 0, items: [] };
      if (scope === 'taken') return taken ? { total: 0, items: [] } : { total: 1, items: [row(2, 'SideA')] };
      return { total: taken ? 2 : 1, items: taken ? [row(1), row(2, 'SideB')] : [row(1)] };
    },
    '/api/clash/collisions/c1': { collision: collision(1), feed: [] },
    '/api/clash/collisions/c2': () => ({ collision: collision(2, taken ? 'SideB' : 'SideA'), feed: [] }),
    '/api/clash/collisions/c1/scene': scene,
    '/api/clash/collisions/c2/scene': scene,
    '/api/clash/collisions/c1/meshes': new ArrayBuffer(4),
    '/api/clash/collisions/c2/meshes': new ArrayBuffer(4),
    'POST /api/clash/collisions/c2/take': () => { taken = true; return null; },
    'POST /api/clash/collisions/c1/assignee': null,
  };
}

describe('My collisions', () => {
  it('MyPage_ToFixByDefault_CountersSwitchScope', async () => {
    renderApp('/clash/my', responses());

    expect(await screen.findByRole('button', { name: 'Коллизия 1' })).toBeTruthy();
    expect(myCalls()[0].get('scope')).toBe('tofix');
    await waitFor(() => expect(posts('/api/clash/my/visit')).toHaveLength(1));

    fireEvent.click(await screen.findByRole('button', { name: /Взяла другая сторона/ }));
    expect(await screen.findByRole('button', { name: 'Коллизия 2' })).toBeTruthy();
    expect(screen.getByText(/взял Пётр/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /Новые с прошлого захода/ }));
    await waitFor(() => expect(myCalls().some((p) => p.get('since') === 'visit' && p.get('scope') === 'all')).toBe(true));
  });

  it('MyPage_NoDisciplines_ShowsHint', async () => {
    renderApp('/clash/my', responses([]));

    expect(await screen.findByText('Разделы не указаны — попросите координатора указать их в профиле')).toBeTruthy();
  });

  it('MyPage_TakeMovesRowToMine', async () => {
    renderApp('/clash/my', responses());
    fireEvent.click(await screen.findByRole('button', { name: /Взяла другая сторона/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Коллизия 2' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Беру' }));

    await waitFor(() => expect(posts('/api/clash/collisions/c2/take')).toHaveLength(1));
    fireEvent.click(screen.getByRole('button', { name: /Мне править/ }));
    expect(await screen.findByRole('button', { name: 'Коллизия 2' })).toBeTruthy();
  });

  it('CollisionCard_HandOverRequiresSide_NoteOptional', async () => {
    renderApp('/clash/my?collision=c1', responses());
    fireEvent.click(await screen.findByRole('button', { name: 'Передать…' }));

    const send = screen.getByRole('button', { name: 'Передать' }) as HTMLButtonElement;
    expect(send.disabled).toBe(true);
    fireEvent.click(screen.getByRole('radio', { name: 'сторона A' }));
    expect(send.disabled).toBe(false);
    fireEvent.change(screen.getByLabelText('Примечание к передаче'), { target: { value: 'Стену двигает КР' } });
    fireEvent.click(send);

    await waitFor(() => expect(posts('/api/clash/collisions/c1/assignee')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/clash/collisions/c1/assignee')[0][1]!.body))).toEqual({ assignee: 'SideA', note: 'Стену двигает КР' });
  });
});

vi.setConfig({ testTimeout: 15_000 });
