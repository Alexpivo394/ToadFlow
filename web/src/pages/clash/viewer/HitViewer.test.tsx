import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { focusManager } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import type { ViewerElementView } from '../../../api/types';
import { Failure, renderApp } from '../../../test/renderApp';

// WebGL в jsdom нет: вместо сцены three.js — заглушка, которая показывает, что ей передали, и умеет «щёлкнуть» элемент.
vi.mock('./ClashViewer', () => ({
  default: (props: { scene: { elements: unknown[] }; meshes?: Map<number, unknown>; isolation: string; sectionBox: unknown; onPick: (i: number | null) => void }) => (
    <div data-testid="viewer">
      <span>в сцене {props.scene.elements.length}</span>
      <span>сеток {props.meshes?.size ?? 0}</span>
      <span>изоляция {props.isolation}</span>
      <span>{props.sectionBox ? 'куб включён' : 'куб выключен'}</span>
      <button onClick={() => props.onPick(2)}>выбрать окружение</button>
    </div>
  ),
}));

const hit = (n: number) => ({
  runId: 'r1', number: n, depthMm: 40 + n, x: 0, y: 0, z: 0,
  modelA: 'a', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', setNamesA: ['Стены'], categoryA: 'OST_Walls',
  familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'b', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', setNamesB: ['Воздуховоды'],
  categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
});

function element(index: number, role: ViewerElementView['role'], uniqueId: string, mesh = true): ViewerElementView {
  return {
    index, role, modelId: role === 'B' ? 'b' : 'a', modelName: role === 'B' ? 'POL_K1_OV.rvt' : 'POL_K1_AR.rvt', discipline: role === 'B' ? 'ОВ' : 'АР',
    uniqueId, elementId: 100 + index, category: 'OST_Walls', family: 'Стена', type: '200', level: 'Этаж 1',
    min: [-100, -100, -100], max: [100, 100, 100], triangles: 12, mesh,
  };
}

const scene = (n: number, truncated = false) => ({
  runId: 'r1', number: n, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 40 + n, meshTriangles: 36, truncated,
  elements: [element(0, 'A', `ua${n}`), element(1, 'B', `ub${n}`), element(2, 'context', 'near'), element(3, 'context', 'far', false)],
});

function meshes(indices: number[]): ArrayBuffer {
  const view = new DataView(new ArrayBuffer(8 + indices.length * (12 + 36 + 12)));
  view.setUint32(0, 0x31564354, true);
  view.setInt32(4, indices.length, true);
  let at = 8;
  for (const index of indices) {
    view.setInt32(at, index, true);
    view.setInt32(at + 4, 3, true);
    view.setInt32(at + 8, 3, true);
    at += 12 + 36;
    for (const i of [0, 1, 2]) { view.setUint32(at, i, true); at += 4; }
  }
  return view.buffer;
}

const base = {
  '/api/clash/parts/p1': { id: 'p1', name: 'Часть', object: null, part: null, updatedAt: '2026-10-28T10:00:00Z', models: [] },
  '/api/clash/parts/p1/draft': { configuration: { groups: [], sets: [], matrix: [], exclusions: [], ignoreConnectedMep: true }, version: 1, disciplines: ['АР', 'ОВ'], models: [], problems: [] },
  '/api/clash/catalog': { categories: [], parameters: [], families: [], types: [], levels: [], worksets: [] },
  '/api/clash/templates': [],
  '/api/clash/models': [],
  '/api/clash/parts/p1/draft/run': null,
  '/api/clash/parts/p1/draft/summary': { total: 2, byDisciplines: [{ disciplinePair: 'АР × ОВ', count: 2, enabled: true, toleranceMm: 20 }], bySets: [] },
  '/api/clash/parts/p1/draft/hits': { total: 2, items: [hit(1), hit(2)] },
  '/api/clash/runs/r1/hits/1/scene': scene(1, true),
  '/api/clash/runs/r1/hits/1/meshes': meshes([0, 1, 2]),
  '/api/clash/runs/r1/hits/2/scene': scene(2),
  '/api/clash/runs/r1/hits/2/meshes': meshes([0, 1]),
  '/api/clash/runs/r1/elements/a/near': {
    modelName: 'POL_K1_AR.rvt', discipline: 'АР', uniqueId: 'near', elementId: 102, category: 'OST_Columns', family: 'Колонна', type: '400x400',
    level: 'Этаж 1', workset: 'КР', parameters: [{ name: 'Марка', text: 'К-1', number: null }, { name: 'Объём', text: '1.2 м³', number: 1.2 }],
  },
};

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls.map(([url]) => url);

async function openHit(responses: Record<string, unknown> = base, row = 1) {
  renderApp('/clash/parts/p1', responses);
  fireEvent.click(await screen.findByRole('tab', { name: 'Черновик' }));
  await screen.findByText(/ID 1001/);
  fireEvent.click(screen.getAllByRole('row')[row]);
  return screen.findByRole('dialog');
}

describe('Hit viewer', () => {
  it('DraftResults_RowClickOpensViewer', async () => {
    const dialog = await openHit();

    expect(within(dialog).getByText(/Коллизия № 1/)).toBeTruthy();
    expect(within(dialog).getByText(/АР × ОВ/)).toBeTruthy();
    await waitFor(() => expect(calls()).toContain('/api/clash/runs/r1/hits/1/scene?radius=5000'));
  });

  it('HitViewer_LoadsSceneAndShowsCounts', async () => {
    const dialog = await openHit();

    expect(await within(dialog).findByText('в сцене 4')).toBeTruthy();
    expect(await within(dialog).findByText('сеток 3')).toBeTruthy();
    expect(within(dialog).getByText('Окружение: 2 элемента, 1 с сеткой')).toBeTruthy();
    expect(within(dialog).getByText('окружение урезано')).toBeTruthy();
  });

  it('HitViewer_PickShowsElementProperties', async () => {
    const dialog = await openHit();

    fireEvent.click(await within(dialog).findByRole('button', { name: 'выбрать окружение' }));

    expect(await within(dialog).findByText('К-1')).toBeTruthy();
    expect(within(dialog).getByText('Колонна · 400x400')).toBeTruthy();
    expect(within(dialog).getByText('1.2 м³')).toBeTruthy();
  });

  it('HitViewer_NextLoadsNextHit', async () => {
    const dialog = await openHit();
    await within(dialog).findByText('сеток 3');

    fireEvent.click(within(dialog).getByRole('button', { name: 'след. →' }));

    expect(await within(dialog).findByText(/Коллизия № 2/)).toBeTruthy();
    expect(await within(dialog).findByText('сеток 2')).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'след. →' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('HitViewer_IsolationPassedToViewer', async () => {
    const dialog = await openHit();
    await within(dialog).findByText('изоляция all');
    expect(within(dialog).getByText('куб выключен')).toBeTruthy();

    fireEvent.click(within(dialog).getByText('Только A и B'));
    fireEvent.click(within(dialog).getByRole('checkbox', { name: 'Секущий куб' }));

    expect(await within(dialog).findByText('изоляция pair')).toBeTruthy();
    expect(within(dialog).getByText('куб включён')).toBeTruthy();
  });

  it('HitViewer_StaleRun_ShowsMessage', async () => {
    const dialog = await openHit({ ...base, '/api/clash/runs/r1/hits/1/scene': new Failure(404, 'результат черновика обновился или коллизия не найдена') });

    expect(await within(dialog).findByText('результат черновика обновился или коллизия не найдена')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Обновить список' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('HitViewer_ListRefreshedByNewRun_KeepsOpenHit', async () => {
    const dialog = await openHit();
    await within(dialog).findByText('сеток 3');

    // Новый прогон закончился, пока окно открыто: список перечитан, под тем же номером строки — другая коллизия другого прогона.
    const fetchMock = vi.mocked(fetch);
    const original = fetchMock.getMockImplementation()!;
    const replaced = { ...hit(1), runId: 'r2', number: 77, disciplineB: 'КР' };
    fetchMock.mockImplementation(async (input, init) =>
      String(input).startsWith('/api/clash/parts/p1/draft/hits')
        ? new Response(JSON.stringify({ total: 1, items: [replaced] }), { status: 200, headers: { 'Content-Type': 'application/json' } })
        : original(input, init));
    focusManager.setFocused(false);
    focusManager.setFocused(true);
    await waitFor(() => expect(calls().filter((url) => url.startsWith('/api/clash/parts/p1/draft/hits')).length).toBeGreaterThan(1));
    await new Promise((r) => setTimeout(r, 300));

    expect(within(dialog).getByText(/Коллизия № 1/)).toBeTruthy();
    expect(calls().some((url) => url.startsWith('/api/clash/runs/r2/'))).toBe(false);
  });
});
