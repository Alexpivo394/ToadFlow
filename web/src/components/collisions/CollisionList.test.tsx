import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { Failure, renderUi } from '../../test/renderApp';
import { bulkFilterOf, CollisionList, type ListSource } from './CollisionList';
import { toQuery, type ListParams } from './useListParams';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const bulkBodies = () => calls().filter(([url, init]) => url === '/api/clash/collisions/bulk' && init?.method === 'POST')
  .map(([, init]) => JSON.parse(String(init!.body)));

const collision = (n: number) => ({
  id: `c${n}`, number: n, status: 'New', assignee: 'Both', foundNow: true, depthMm: 40, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'DON_K1_KR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'КР', categoryA: 'OST_Walls', familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'DON_K1_OVK.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВК', categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k'], firstVersion: 1, lastFoundVersion: 1, suggestedPredecessorId: null, suggestedPredecessorNumber: null, imageVersion: '',
});

const group = (id: number, count: number) => ({
  model: 'm2', modelName: 'DON_K1_OVK.rvt', uniqueId: `ub${id}`, elementId: 2000 + id, discipline: 'ОВК', category: 'OST_DuctCurves',
  family: 'Воздуховод', type: '500x300', level: 'Этаж 1', count, maxDepthMm: 40,
});

const source: ListSource = { list: (p) => `/api/clash/parts/p1/collisions?${toQuery(p)}&pageSize=50`, partId: 'p1' };
const base: ListParams = { status: 'open', sort: 'depth', group: 'none', page: 1 };

function list(params: ListParams, responses: Record<string, unknown>) {
  return renderUi(<CollisionList source={source} params={params} selectedId={null} onSelect={() => {}} bulk />, responses);
}

const flat = { '/api/clash/parts/p1/collisions': { total: 120, items: [collision(1), collision(2)] } };

describe('CollisionList', () => {
  it('List_GroupByElement_ShowsHeadersAndCounts', async () => {
    list({ ...base, group: 'element', side: 'ОВК' }, {
      '/api/clash/parts/p1/collisions': (url: string) => new URL(url, 'http://x').searchParams.has('element')
        ? { total: 2, items: [collision(1), collision(3)] }
        : { groups: 2, collisions: 3, items: [group(1, 2), group(2, 1)] },
    });

    expect(await screen.findByText('Элементов: 2 · коллизий: 3')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Группа ID 2001 — 2' }));
    expect(await screen.findByRole('button', { name: 'Коллизия 3' })).toBeTruthy();
    const expanded = calls().map(([url]) => new URL(url, 'http://x').searchParams).find((p) => p.has('element'))!;
    expect([expanded.get('element'), expanded.has('group')]).toEqual(['m2:ub1', false]);
  });

  it('List_GroupCheckbox_SelectsAll', async () => {
    list({ ...base, group: 'element', side: 'ОВК' }, {
      '/api/clash/parts/p1/collisions': (url: string) => new URL(url, 'http://x').searchParams.has('element')
        ? { total: 2, items: [collision(1), collision(3)] }
        : { groups: 2, collisions: 3, items: [group(1, 2), group(2, 1)] },
      'POST /api/clash/collisions/bulk': { done: 2, refused: [] },
    });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить группу ID 2001' }));
    expect(await screen.findByText('Выделено 2')).toBeTruthy();
    const ids = calls().map(([url]) => new URL(url, 'http://x').searchParams).find((p) => p.has('element') && p.get('pageSize') === '200');
    expect(ids?.get('element')).toBe('m2:ub1');

    fireEvent.click(screen.getByRole('button', { name: 'Беру' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Продолжить' }));
    await waitFor(() => expect(bulkBodies()).toHaveLength(1));
    expect(bulkBodies()[0]).toMatchObject({ partId: 'p1', action: 'Take', ids: ['c1', 'c3'] });
  });

  it('List_SelectAllByFilter_SendsFilterAndExpectedCount', async () => {
    list({ ...base, pair: 'КР × ОВК' }, { ...flat, 'POST /api/clash/collisions/bulk': { done: 120, refused: [] } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Выделить все по фильтру (120)' }));
    expect(screen.getByText('Выделено 120')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Беру' }));
    expect(await screen.findByText('Изменить 120 коллизий: «Беру». Продолжить?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить' }));

    await waitFor(() => expect(bulkBodies()).toHaveLength(1));
    const body = bulkBodies()[0];
    expect(body).toMatchObject({ partId: 'p1', action: 'Take', expectedCount: 120, filter: { status: 'New,Active,Returned,FixedPending,Unchecked', pair: 'КР × ОВК' } });
    expect(body.ids).toBeUndefined();
  });

  it('List_Bulk409_ShowsNewCount', async () => {
    let first = true;
    list(base, {
      ...flat,
      'POST /api/clash/collisions/bulk': () => {
        if (!first) return { done: 118, refused: [] };
        first = false;
        return new Failure(409, 'Коллизий по фильтру теперь 118 — проверьте и повторите', { count: 118 });
      },
    });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Выделить все по фильтру (120)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Исправлены' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Продолжить' }));

    expect(await screen.findByText('Коллизий по фильтру теперь 118 — проверьте и повторите')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить (118)' }));
    await waitFor(() => expect(bulkBodies()).toHaveLength(2));
    expect(bulkBodies()[1]).toMatchObject({ action: 'FixedPending', expectedCount: 118 });
  });

  it('List_BulkRefusals_Listed', async () => {
    list(base, { ...flat, 'POST /api/clash/collisions/bulk': { done: 1, refused: [{ number: 2, reason: 'Отметить исправленной может только своя сторона' }] } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выделить №2' }));
    fireEvent.click(screen.getByRole('button', { name: 'Исправлены' }));
    expect(await screen.findByText('Изменить 2 коллизии: «Исправлены». Продолжить?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить' }));

    expect(await screen.findByText('№2 — Отметить исправленной может только своя сторона')).toBeTruthy();
    expect(bulkBodies()[0]).toMatchObject({ action: 'FixedPending', ids: ['c1', 'c2'] });
  });

  it('List_FakeRequiresNote', async () => {
    list(base, { ...flat, 'POST /api/clash/collisions/bulk': { done: 1, refused: [] } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Фиктивные…' }));
    const dialog = await screen.findByRole('dialog');
    const go = within(dialog).getByRole('button', { name: 'Продолжить' }) as HTMLButtonElement;
    expect(go.disabled).toBe(true);
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Примечание' }), { target: { value: 'Отверстие есть в КР' } });
    expect(go.disabled).toBe(false);
    fireEvent.click(go);

    await waitFor(() => expect(bulkBodies()).toHaveLength(1));
    expect(bulkBodies()[0]).toMatchObject({ action: 'Fake', note: 'Отверстие есть в КР', ids: ['c1'] });
  });
});

const range = (from: number, count: number) => Array.from({ length: count }, (_, i) => collision(from + i));
const searchOf = (url: string) => new URL(url, 'http://x').searchParams;

function Harness({ initial, sources }: { initial: ListParams; sources: ListSource[] }) {
  const [params, setParams] = useState(initial);
  const [which, setWhich] = useState(0);
  return (
    <>
      <button type="button" onClick={() => setWhich(1)}>другой источник</button>
      <CollisionList source={sources[which]} params={params} selectedId={null} onSelect={() => {}} bulk
        onParams={(patch) => setParams((p) => ({ ...p, ...patch }))} />
    </>
  );
}

const myRow = (n: number, partId: string, partName: string) => ({
  collision: collision(n), partId, partName, object: null, part: null, mineToFix: true, takenBy: null, takenAt: null, unseen: false,
});

describe('CollisionList after review', () => {
  it('List_GroupCheckbox_PagesBy200UntilGroupCount', async () => {
    list({ ...base, group: 'element', side: 'ОВК' }, {
      '/api/clash/parts/p1/collisions': (url: string) => {
        const q = searchOf(url);
        if (!q.has('element')) return { groups: 1, collisions: 450, items: [group(1, 450)] };
        const page = Number(q.get('page'));
        const size = Number(q.get('pageSize'));
        return { total: 450, items: range((page - 1) * size + 1, Math.max(0, Math.min(size, 450 - (page - 1) * size))) };
      },
    });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить группу ID 2001' }));
    expect(await screen.findByText('Выделено 450')).toBeTruthy();
    const pages = calls().map(([url]) => searchOf(url)).filter((p) => p.has('element'));
    expect(pages.map((p) => p.get('pageSize'))).toEqual(['200', '200', '200']);
    expect(pages.map((p) => p.get('page'))).toEqual(['1', '2', '3']);
  });

  it('List_ExpandedGroup_ShowMore', async () => {
    list({ ...base, group: 'element', side: 'ОВК' }, {
      '/api/clash/parts/p1/collisions': (url: string) => {
        const q = searchOf(url);
        if (!q.has('element')) return { groups: 1, collisions: 60, items: [group(1, 60)] };
        return q.get('page') === '2' ? { total: 60, items: range(51, 10) } : { total: 60, items: range(1, 50) };
      },
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Группа ID 2001 — 60' }));
    expect(await screen.findByText('Показано 50 из 60')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё' }));
    expect(await screen.findByRole('button', { name: 'Коллизия 60' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).toBeNull();
  });

  it('List_SourceChange_ResetsSelection', async () => {
    const other: ListSource = { ...source, list: (p) => `${source.list(p)}&check=k` };
    renderUi(<Harness initial={base} sources={[source, other]} />, flat);

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    expect(screen.getByText('Выделено 1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'другой источник' }));
    await waitFor(() => expect(screen.queryByText('Выделено 1')).toBeNull());
  });

  it('List_ShiftRange_NotAcrossPages', async () => {
    renderUi(<Harness initial={base} sources={[source]} />, {
      '/api/clash/parts/p1/collisions': (url: string) =>
        searchOf(url).get('page') === '2' ? { total: 120, items: [collision(3), collision(4)] } : { total: 120, items: [collision(1), collision(2)] },
    });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('button', { name: '→' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №4' }), { shiftKey: true });
    expect(await screen.findByText('Выделено 2')).toBeTruthy();
  });

  it('List_PageChange_KeepsPreviousRowsWhileLoading', async () => {
    renderUi(<Harness initial={base} sources={[source]} />, flat);
    await screen.findByRole('button', { name: 'Коллизия 1' });
    const real = fetch;
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) =>
      searchOf(url).get('page') === '2' ? new Promise(() => {}) : real(url, init)));

    fireEvent.click(screen.getByRole('button', { name: '→' }));
    await waitFor(() => expect((fetch as unknown as FetchMock).mock.calls.some(([url]) => searchOf(url).get('page') === '2')).toBe(true));
    expect(screen.getByRole('button', { name: 'Коллизия 1' })).toBeTruthy();
  });

  it('List_AllByFilter_OverLimit_Disabled', async () => {
    list(base, { '/api/clash/parts/p1/collisions': { total: 6000, items: [collision(1)] } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    const button = screen.getByRole('button', { name: /больше 5\s000 — сузьте фильтр/ }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });

  it('List_ManyRefusals_ShowsFirst20', async () => {
    const refused = Array.from({ length: 25 }, (_, i) => ({ number: 100 + i, reason: 'нельзя' }));
    list(base, { ...flat, 'POST /api/clash/collisions/bulk': { done: 0, refused } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Исправлены' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Продолжить' }));

    expect(await screen.findByText('№119 — нельзя')).toBeTruthy();
    expect(screen.queryByText('№120 — нельзя')).toBeNull();
    expect(screen.getByText('ещё 5')).toBeTruthy();
  });

  it('List_BulkUnchanged_Shown', async () => {
    list(base, { ...flat, 'POST /api/clash/collisions/bulk': { done: 1, refused: [], unchanged: 1 } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выделить №2' }));
    fireEvent.click(screen.getByRole('button', { name: 'Беру' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Продолжить' }));

    expect(await screen.findByText(/без изменений: 1/)).toBeTruthy();
  });

  it('List_MyBulk_PartFails_KeepsOthersDone', async () => {
    const my: ListSource = { list: (p) => `/api/clash/my?${toQuery(p)}&pageSize=50`, my: true };
    renderUi(<CollisionList source={my} params={base} selectedId={null} onSelect={() => {}} bulk />, {
      '/api/clash/my': { total: 2, items: [myRow(1, 'p1', 'Корпус 1'), myRow(2, 'p2', 'Корпус 2')] },
      'POST /api/clash/collisions/bulk': (_url: string, init?: RequestInit) =>
        JSON.parse(String(init!.body)).partId === 'p1' ? new Failure(500, 'сбой сервера') : { done: 1, refused: [] },
    });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Выделить №2' }));
    fireEvent.click(screen.getByRole('button', { name: 'Беру' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Продолжить' }));

    expect(await screen.findByText('Часть «Корпус 1»: сбой сервера')).toBeTruthy();
    expect(screen.getByText(/Готово: 1/)).toBeTruthy();
    expect(screen.getByText('Выделено 1')).toBeTruthy(); // осталась только не изменённая часть
    expect(bulkBodies().map((b) => b.partId)).toEqual(['p1', 'p2']);
  });
});

describe('bulkFilterOf', () => {
  it('BulkFilter_My_NewForMe_EqualsListQuery', () => {
    const my: ListSource = {
      list: (p) => {
        const q = new URLSearchParams(toQuery(p));
        q.set('since', 'unseen');
        q.set('scope', 'all');
        q.set('partId', 'p1');
        return `/api/clash/my?${q}&pageSize=50`;
      },
      partId: 'p1',
      my: true,
    };
    const filter = bulkFilterOf({ ...base, assignee: 'mine', q: '12' }, my);
    expect(filter).toEqual({ my: true, scope: 'all', since: 'unseen', status: 'New,Active,Returned,FixedPending,Unchecked', q: '12' });
  });

  it('BulkFilter_Approved_HasCheckAndElement', () => {
    const approved: ListSource = { list: (p) => `/api/clash/parts/p1/approved/collisions?${toQuery(p)}&check=k&element=m2%3Aub1&pageSize=50`, partId: 'p1' };
    expect(bulkFilterOf({ ...base, level: 'Этаж 1', stuckDays: 7 }, approved)).toEqual({
      status: 'New,Active,Returned,FixedPending,Unchecked', level: 'Этаж 1', stuckDays: 7, check: 'k', element: 'm2:ub1',
    });
  });
});

describe('CollisionList seen action', () => {
  const my: ListSource = { list: (p) => `/api/clash/my?${toQuery(p)}&pageSize=50`, my: true };
  const myPage = { '/api/clash/my': { total: 1, items: [myRow(1, 'p1', 'Корпус 1')] } };

  it('Seen_OnlyInMy_AndOnlyWhenCanWork', async () => {
    const first = list(base, flat);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    expect(screen.getByRole('button', { name: 'Беру' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Отметить просмотренными' })).toBeNull();
    first.unmount();

    const mine = renderUi(<CollisionList source={my} params={base} selectedId={null} onSelect={() => {}} bulk />, myPage);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    expect(screen.getByRole('button', { name: 'Отметить просмотренными' })).toBeTruthy();
    mine.unmount();

    renderUi(<CollisionList source={my} params={base} selectedId={null} onSelect={() => {}} bulk canEdit={false} />, myPage);
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    expect(screen.queryByRole('button', { name: 'Отметить просмотренными' })).toBeNull();
  });

  it('Unseen_IsDotWithExplanation', async () => {
    renderUi(<CollisionList source={my} params={base} selectedId={null} onSelect={() => {}} />,
      { '/api/clash/my': { total: 1, items: [{ ...myRow(1, 'p1', 'Корпус 1'), unseen: true }] } });

    const dot = await screen.findByRole('img', { name: /Вы ещё не открывали эту коллизию/ });
    expect(dot.textContent).toBe('');
    expect(screen.queryByText('нов')).toBeNull();
  });
});

describe('CollisionList: who fixes', () => {
  it('Bulk_AssigneeByDisciplines', async () => {
    list(base, { ...flat, 'POST /api/clash/collisions/bulk': { done: 1, refused: [] } });

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Выделить №1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Кто правит ▾' }));

    expect(await screen.findByRole('menuitem', { name: 'ОВК' })).toBeTruthy();
    expect(screen.getByRole('menuitem', { name: 'КР' })).toBeTruthy();
    expect(screen.getByRole('menuitem', { name: 'КР и ОВК' })).toBeTruthy();
    expect(screen.queryByRole('menuitem', { name: 'Сторона A' })).toBeNull();
  });
});
