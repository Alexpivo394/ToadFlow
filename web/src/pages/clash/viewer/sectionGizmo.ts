import type { ViewerElementView } from '../../../api/types';
import type { Box, Vec3 } from '../../../lib/viewer';

/** Грани секущего куба не сходятся ближе этого, мм. */
export const MinGapMm = 50;

/** Ручка грани: ось грани, сторона и центр грани. */
export interface Handle {
  axis: 0 | 1 | 2;
  side: 'min' | 'max';
  position: Vec3;
}

/** Шесть ручек в центрах граней куба. */
export function handles(box: Box): Handle[] {
  const center = [0, 1, 2].map((i) => (box.min[i] + box.max[i]) / 2) as Vec3;
  return ([0, 1, 2] as const).flatMap((axis) =>
    (['min', 'max'] as const).map((side) => {
      const position = [...center] as Vec3;
      position[axis] = box[side][axis];
      return { axis, side, position };
    }),
  );
}

/**
 * Перетаскивание грани: луч мыши проецируется на ось грани, проходящую через точку, где начали тянуть; грань сдвигается
 * на смещение вдоль оси и не подходит к противоположной ближе <see cref="MinGapMm" />. Луч вдоль оси — грань стоит.
 */
export function dragFace(box: Box, handle: Handle, rayOrigin: Vec3, rayDirection: Vec3, startPoint: Vec3): Box {
  const k = handle.axis;
  const dot = (a: Vec3, b: Vec3) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const axis: Vec3 = [0, 0, 0];
  axis[k] = 1;
  const w0: Vec3 = [startPoint[0] - rayOrigin[0], startPoint[1] - rayOrigin[1], startPoint[2] - rayOrigin[2]];
  const b = dot(axis, rayDirection);
  const c = dot(rayDirection, rayDirection);
  const denominator = c - b * b;
  if (Math.abs(denominator) < 1e-9) return box;
  const s = (b * dot(rayDirection, w0) - c * dot(axis, w0)) / denominator;
  const value = startPoint[k] + s;

  const min = [...box.min] as Vec3;
  const max = [...box.max] as Vec3;
  if (handle.side === 'min') min[k] = Math.min(value, max[k] - MinGapMm);
  else max[k] = Math.max(value, min[k] + MinGapMm);
  return { min, max };
}

/** Куб по габариту элемента с запасом (двойной щелчок по элементу). */
export function elementBox(element: ViewerElementView, marginMm = 500): Box {
  return {
    min: element.min.map((v) => v - marginMm) as Vec3,
    max: element.max.map((v) => v + marginMm) as Vec3,
  };
}
