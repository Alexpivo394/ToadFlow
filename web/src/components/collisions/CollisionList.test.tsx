import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Failure, renderUi } from '../../test/renderApp';
import { CollisionList, type ListSource } from './CollisionList';
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
    const ids = calls().map(([url]) => new URL(url, 'http://x').searchParams).find((p) => p.get('pageSize') === '5000');
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
