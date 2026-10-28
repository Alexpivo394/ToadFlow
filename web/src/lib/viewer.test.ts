import { describe, expect, it } from 'vitest';
import type { ViewerElementView, ViewerSceneView } from '../api/types';
import { defaultSectionBox, isVisible, parseMeshes } from './viewer';

function meshes(items: { index: number; positions: number[]; indices: number[] }[]): ArrayBuffer {
  const size = 8 + items.reduce((sum, m) => sum + 12 + m.positions.length * 4 + m.indices.length * 4, 0);
  const view = new DataView(new ArrayBuffer(size));
  view.setUint32(0, 0x31564354, true);
  view.setInt32(4, items.length, true);
  let at = 8;
  for (const m of items) {
    view.setInt32(at, m.index, true);
    view.setInt32(at + 4, m.positions.length / 3, true);
    view.setInt32(at + 8, m.indices.length, true);
    at += 12;
    for (const p of m.positions) { view.setFloat32(at, p, true); at += 4; }
    for (const i of m.indices) { view.setUint32(at, i, true); at += 4; }
  }
  return view.buffer;
}

function element(index: number, role: ViewerElementView['role'], min: number[], max: number[]): ViewerElementView {
  return {
    index, role, modelId: 'm', modelName: 'M.rvt', discipline: 'АР', uniqueId: `u${index}`, elementId: index, category: 'OST_Walls',
    family: null, type: null, level: null, min: min as [number, number, number], max: max as [number, number, number], triangles: 12, mesh: true,
  };
}

const scene = (elements: ViewerElementView[]): ViewerSceneView => ({
  runId: 'r', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements, meshTriangles: 0, truncated: false,
});

describe('viewer', () => {
  it('parseMeshes_ReadsTwoMeshes', () => {
    const buffer = meshes([
      { index: 0, positions: [0, 0, 0, 1, 0, 0, 0, 1, 0], indices: [0, 1, 2] },
      { index: 3, positions: [-75.25, 2, 3, 4, 5, 6, 7, 8, 9, 1, 1, 1], indices: [0, 1, 2, 0, 2, 3] },
    ]);

    const result = parseMeshes(buffer);

    expect([...result.keys()]).toEqual([0, 3]);
    expect(Array.from(result.get(3)!.positions.slice(0, 3))).toEqual([-75.25, 2, 3]);
    expect(Array.from(result.get(3)!.indices)).toEqual([0, 1, 2, 0, 2, 3]);
    expect(result.get(0)!.positions.length).toBe(9);
  });

  it('parseMeshes_Truncated_Throws', () => {
    const buffer = meshes([{ index: 0, positions: [0, 0, 0, 1, 0, 0, 0, 1, 0], indices: [0, 1, 2] }]);

    expect(() => parseMeshes(buffer.slice(0, buffer.byteLength - 4))).toThrow('Повреждённый ответ сеток');
    expect(() => parseMeshes(new ArrayBuffer(8))).toThrow('Повреждённый ответ сеток');
  });

  it('defaultSectionBox_UnionOfPairExpanded', () => {
    const box = defaultSectionBox(scene([
      element(0, 'A', [-100, -50, 0], [0, 50, 100]),
      element(1, 'B', [-20, -10, -30], [300, 10, 20]),
      element(2, 'context', [-9000, -9000, -9000], [9000, 9000, 9000]),
    ]));

    expect(box).toEqual({ min: [-1100, -1050, -1030], max: [1300, 1050, 1100] });
  });

  it('isVisible_PairHidesContext', () => {
    const a = element(0, 'A', [0, 0, 0], [1, 1, 1]);
    const context = element(2, 'context', [0, 0, 0], [1, 1, 1]);

    expect(isVisible(a, 'pair', null)).toBe(true);
    expect(isVisible(context, 'pair', null)).toBe(false);
    expect(isVisible(context, 'all', null)).toBe(true);
  });

  it('isVisible_SelectedWithoutSelection_FallsBackToPair', () => {
    const a = element(0, 'A', [0, 0, 0], [1, 1, 1]);
    const context = element(2, 'context', [0, 0, 0], [1, 1, 1]);

    expect(isVisible(a, 'selected', null)).toBe(true);
    expect(isVisible(context, 'selected', null)).toBe(false);
    expect(isVisible(context, 'selected', 2)).toBe(true);
    expect(isVisible(a, 'selected', 2)).toBe(false);
  });
});
