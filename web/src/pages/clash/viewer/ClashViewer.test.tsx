import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import ClashViewer from './ClashViewer';

const renderer = vi.hoisted(() => ({
  instances: [] as { dispose: ReturnType<typeof vi.fn>; forceContextLoss: ReturnType<typeof vi.fn> }[],
}));

// WebGL в jsdom нет: рендерер — заглушка, остальной three.js настоящий.
vi.mock('three', async (importOriginal) => {
  const three = await importOriginal<typeof import('three')>();
  class FakeRenderer {
    domElement = document.createElement('canvas');
    localClippingEnabled = false;
    dispose = vi.fn();
    forceContextLoss = vi.fn();
    constructor() {
      renderer.instances.push(this);
    }
    setPixelRatio() {}
    setClearColor() {}
    setSize() {}
    render() {}
  }
  return { ...three, WebGLRenderer: FakeRenderer };
});

vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });

const scene = { runId: 'r1', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [], meshTriangles: 0, truncated: false };

describe('Clash viewer', () => {
  it('Viewer_Unmount_ForcesContextLoss', () => {
    const view = render(<ClashViewer scene={scene} isolation="all" sectionBox={null} selected={null} onPick={() => {}} />);
    const created = renderer.instances.at(-1)!;
    expect(created.forceContextLoss).not.toHaveBeenCalled();

    view.unmount();

    expect(created.dispose).toHaveBeenCalled();
    expect(created.forceContextLoss).toHaveBeenCalledTimes(1);
  });
});
