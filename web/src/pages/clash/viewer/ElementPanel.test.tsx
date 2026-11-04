import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ViewerSceneView } from '../../../api/types';
import { renderUi } from '../../../test/renderApp';
import { ElementPanel } from './ElementPanel';

const element = {
  index: 0, role: 'A' as const, modelId: 'm1', modelName: 'POL_K1_AR.rvt', discipline: 'АР', uniqueId: 'ua1', elementId: 1001,
  category: 'OST_Walls', family: 'Стена', type: '200', level: 'Этаж 1', min: [0, 0, 0] as [number, number, number],
  max: [1, 1, 1] as [number, number, number], triangles: 12, mesh: true,
};
const scene: ViewerSceneView = { runId: 'r1', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [element], meshTriangles: 12, truncated: false };

describe('Element panel', () => {
  it('ElementPanel_CategoryLabel', async () => {
    renderUi(<ElementPanel runId="r1" collisionId="c1" scene={scene} selected={0} />, {
      '/api/clash/collisions/c1/elements/m1/ua1': {
        modelName: 'POL_K1_AR.rvt', discipline: 'АР', category: 'OST_Walls', family: 'Стена', type: '200', level: 'Этаж 1', workset: null,
        elementId: 1001, uniqueId: 'ua1', parameters: [],
      },
    });

    expect(await screen.findByText('Стены')).toBeTruthy();
    expect(screen.getAllByText(/^Стены · Стена · 200$/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/OST_Walls/)).toBeNull();
  });
});
