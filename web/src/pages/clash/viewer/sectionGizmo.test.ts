import { describe, expect, it } from 'vitest';
import type { ViewerElementView } from '../../../api/types';
import type { Box } from '../../../lib/viewer';
import { dragFace, elementBox, handles } from './sectionGizmo';

const box: Box = { min: [-1000, -500, -200], max: [1000, 500, 800] };

describe('section gizmo', () => {
  it('gizmo_SixHandlesAtFaceCenters', () => {
    const list = handles(box);

    expect(list).toHaveLength(6);
    expect(list.find((h) => h.axis === 0 && h.side === 'min')!.position).toEqual([-1000, 0, 300]);
    expect(list.find((h) => h.axis === 2 && h.side === 'max')!.position).toEqual([0, 0, 800]);
  });

  it('gizmo_DragMovesOnlyItsFace', () => {
    const handle = handles(box).find((h) => h.axis === 0 && h.side === 'max')!;
    // Камера смотрит сверху вниз; мышь сдвинулась на 300 мм по X.
    const moved = dragFace(box, handle, [handle.position[0] + 300, 0, 5000], [0, 0, -1], handle.position);

    expect(moved.max[0]).toBeCloseTo(1300);
    expect(moved.min).toEqual(box.min);
    expect(moved.max.slice(1)).toEqual(box.max.slice(1));
  });

  it('gizmo_MinGap50mm', () => {
    const handle = handles(box).find((h) => h.axis === 1 && h.side === 'min')!;

    const moved = dragFace(box, handle, [0, 5000, 300], [0, 0, -1], handle.position);

    expect(moved.min[1]).toBe(450); // не ближе 50 мм к противоположной грани
  });

  it('gizmo_ElementBoxPlus500', () => {
    const element = { min: [-10, -20, -30], max: [10, 20, 30] } as unknown as ViewerElementView;

    expect(elementBox(element)).toEqual({ min: [-510, -520, -530], max: [510, 520, 530] });
  });
});
