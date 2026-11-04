import * as THREE from 'three';
import type { Box } from '../../../lib/viewer';

/**
 * Шесть плоскостей секущего куба: −X, +X, −Y, +Y, −Z, +Z. Создаются один раз на просмотрщик; материалы держат ссылку
 * на этот массив, поэтому перетаскивание грани меняет только <c>constant</c> и не перекомпилирует материалы.
 */
export function createSectionPlanes(): THREE.Plane[] {
  return [0, 1, 2].flatMap((axis) => {
    const normal = new THREE.Vector3();
    normal.setComponent(axis, 1);
    return [new THREE.Plane(normal.clone(), 0), new THREE.Plane(normal.clone().negate(), 0)];
  });
}

/** Ставит плоскости на грани куба (мм от точки коллизии); отсекается всё снаружи. */
export function updateSectionPlanes(planes: THREE.Plane[], box: Box): void {
  for (const axis of [0, 1, 2]) {
    planes[axis * 2].constant = -box.min[axis];
    planes[axis * 2 + 1].constant = box.max[axis];
  }
}
