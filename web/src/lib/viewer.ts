import type { ViewerElementView, ViewerSceneView } from '../api/types';

/** Сетка элемента сцены: позиции в мм относительно точки коллизии и индексы треугольников. */
export interface ViewerMesh {
  positions: Float32Array;
  indices: Uint32Array;
}

export type Vec3 = [number, number, number];

export interface Box {
  min: Vec3;
  max: Vec3;
}

/** «Всё», «только A и B», «только выбранный» (без выбора — как «только A и B»). */
export type Isolation = 'all' | 'pair' | 'selected';

const Magic = 0x31564354; // "TCV1"

/** Ответ <c>/meshes</c> сервера (формат TCV1, little-endian) → сетки по индексу элемента в манифесте сцены. */
export function parseMeshes(buffer: ArrayBuffer): Map<number, ViewerMesh> {
  const broken = () => new Error('Повреждённый ответ сеток');
  if (buffer.byteLength < 8) throw broken();
  const view = new DataView(buffer);
  if (view.getUint32(0, true) !== Magic) throw broken();
  const count = view.getInt32(4, true);
  const result = new Map<number, ViewerMesh>();
  let at = 8;
  for (let i = 0; i < count; i++) {
    if (at + 12 > buffer.byteLength) throw broken();
    const index = view.getInt32(at, true);
    const vertices = view.getInt32(at + 4, true);
    const indices = view.getInt32(at + 8, true);
    at += 12;
    const end = at + vertices * 12 + indices * 4;
    if (vertices < 0 || indices < 0 || end > buffer.byteLength) throw broken();
    // Все поля по 4 байта, поэтому смещения выровнены и массивы смотрят прямо в буфер.
    const positions = new Float32Array(buffer, at, vertices * 3);
    const triangles = new Uint32Array(buffer, at + vertices * 12, indices);
    result.set(index, { positions, indices: triangles });
    at = end;
  }

  if (at !== buffer.byteLength) throw broken();
  return result;
}

/** Секущий куб по умолчанию: габарит A и B, расширенный на метр с каждой стороны. */
export function defaultSectionBox(scene: ViewerSceneView, marginMm = 1000): Box {
  const pair = scene.elements.filter((e) => e.role !== 'context');
  const min = [0, 1, 2].map((axis) => Math.min(...pair.map((e) => e.min[axis])) - marginMm) as Vec3;
  const max = [0, 1, 2].map((axis) => Math.max(...pair.map((e) => e.max[axis])) + marginMm) as Vec3;
  return { min, max };
}

export function isVisible(element: ViewerElementView, isolation: Isolation, selected: number | null): boolean {
  if (isolation === 'all') return true;
  if (isolation === 'selected' && selected != null) return element.index === selected;
  return element.role !== 'context';
}

/** Цвета сторон: A — красный, B — синий, окружение — полупрозрачный серый (габариты без сетки — каркас того же цвета). */
export function roleColor(role: ViewerElementView['role']): { color: string; opacity: number } {
  if (role === 'A') return { color: '#e03131', opacity: 1 };
  if (role === 'B') return { color: '#1971c2', opacity: 1 };
  if (role === 'set') return { color: '#2f9e44', opacity: 1 };
  return { color: '#adb5bd', opacity: 0.2 };
}

/**
 * Кадр камеры коллизии: пересечение габаритов A и B (там, где элементы проходят друг через друга), расширенное на
 * <c>marginMm</c> (1,5 м) с каждой стороны. Если габариты не пересекаются (касание по допуску) — их общий габарит. Камера
 * смотрит в центр кадра и отходит так, чтобы он целиком был виден.
 */
export function frameForHit(scene: ViewerSceneView, marginMm = 1500): Box & { center: Vec3; radius: number } {
  const pair = scene.elements.filter((e) => e.role === 'A' || e.role === 'B');
  const lo = [0, 1, 2].map((axis) => Math.max(...pair.map((e) => e.min[axis])));
  const hi = [0, 1, 2].map((axis) => Math.min(...pair.map((e) => e.max[axis])));
  const overlap = pair.length > 0 && lo.every((v, axis) => v <= hi[axis]);
  const min = (overlap ? lo : [0, 1, 2].map((axis) => Math.min(0, ...pair.map((e) => e.min[axis])))).map((v) => v - marginMm) as Vec3;
  const max = (overlap ? hi : [0, 1, 2].map((axis) => Math.max(0, ...pair.map((e) => e.max[axis])))).map((v) => v + marginMm) as Vec3;
  const center = [0, 1, 2].map((axis) => (min[axis] + max[axis]) / 2) as Vec3;
  const radius = Math.hypot(...[0, 1, 2].map((axis) => (max[axis] - min[axis]) / 2));
  return { min, max, center, radius };
}
