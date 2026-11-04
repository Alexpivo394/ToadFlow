import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { createSectionPlanes, updateSectionPlanes } from './sectionPlanes';

describe('Section planes', () => {
  it('UpdateSectionPlanes_MovesConstantsOfSamePlanes', () => {
    const planes = createSectionPlanes();
    const same = [...planes];
    const normals = planes.map((p) => p.normal.clone());

    updateSectionPlanes(planes, { min: [-100, -200, -300], max: [100, 200, 300] });
    updateSectionPlanes(planes, { min: [-10, -20, -30], max: [10, 20, 40] });

    expect(planes).toHaveLength(6);
    planes.forEach((p, i) => {
      expect(p).toBe(same[i]);
      expect(p.normal.equals(normals[i])).toBe(true);
    });
    const kept = (point: [number, number, number]) => planes.every((p) => p.distanceToPoint(new THREE.Vector3(...point)) >= 0);
    expect(kept([0, 0, 0])).toBe(true);
    expect(kept([9, 19, 39])).toBe(true);
    expect(kept([11, 0, 0])).toBe(false);
    expect(kept([0, 0, -31])).toBe(false);
  });
});
