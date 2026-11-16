import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { focusManager } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { renderUi } from '../../../test/renderApp';
import { mySide } from '../my/MyPage';
import { HitScene } from './HitScene';

// WebGL в jsdom нет: вместо сцены three.js — заглушка.
vi.mock('./ClashViewer', () => ({
  default: ({ meshes }: { meshes?: Map<number, unknown> }) => <div data-meshes={meshes ? [...meshes.keys()].sort().join(',') : ''}>сцена</div>,
}));

/** Ответ TCV1 с пустыми сетками элементов <c>indexes</c>. */
const tcv = (...indexes: number[]) => new Uint32Array([0x31564354, indexes.length, ...indexes.flatMap((i) => [i, 0, 0])]).buffer;

const element = (index: number, role: 'A' | 'B') => ({
  index, role, modelId: 'm1', modelName: 'POL_K1_AR.rvt', discipline: 'АР', uniqueId: `u${index}`, elementId: 1000 + index,
  category: 'OST_Walls', family: 'Стена', type: '200', level: 'Этаж 1', min: [0, 0, 0], max: [1, 1, 1], triangles: 12, mesh: true,
});
const scene = { runId: 'r1', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [element(0, 'A'), element(1, 'B')], meshTriangles: 24, truncated: false };

describe('Hit scene', () => {
  it('HitScene_PropertiesCollapsedByDefault', async () => {
    renderUi(<HitScene runId="" number={1} collisionId="c1" />, {
      '/api/clash/collisions/c1/scene': scene,
      '/api/clash/collisions/c1/meshes': new Uint32Array([0x31564354, 0]).buffer,
    });

    expect(await screen.findByText('сцена')).toBeTruthy();
    expect(screen.getByText('Щёлкните элемент в сцене, чтобы выбрать его')).toBeTruthy();
    expect(screen.queryByText('Элемент A')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Свойства' }));

    expect(await screen.findByText('Элемент A')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Скрыть свойства' })).toBeTruthy();
  });
});

describe('Hit scene: cache', () => {
  it('Viewer_Meshes_NotRefetchedOnFocus', async () => {
    renderUi(<HitScene runId="" number={1} collisionId="c1" version="v1" />, {
      '/api/clash/collisions/c1/scene': scene,
      '/api/clash/collisions/c1/meshes': () => new Uint32Array([0x31564354, 0]).buffer,
    });
    expect(await screen.findByText('сцена')).toBeTruthy();
    const meshCalls = () => vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('/meshes')).length;
    // Пара и окружение — по запросу.
    await waitFor(() => expect(meshCalls()).toBe(2));

    act(() => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(meshCalls()).toBe(2);
    focusManager.setFocused(undefined);
  });
});

describe('Hit scene: pair first', () => {
  it('HitScene_PairAndContextMeshes_RequestedWithSceneAndMerged', async () => {
    const withContext = { ...scene, elements: [...scene.elements, { ...element(2, 'A'), role: 'context' }] };
    renderUi(<HitScene runId="" number={1} collisionId="c1" />, {
      '/api/clash/collisions/c1/scene': withContext,
      '/api/clash/collisions/c1/meshes': (url: string) => (url.includes('part=pair') ? tcv(0, 1) : url.includes('part=context') ? tcv(2) : tcv()),
    });

    const urls = () => vi.mocked(fetch).mock.calls.map(([url]) => String(url));
    // Сцена, пара и окружение — три запроса.
    await waitFor(() => expect(urls().filter((u) => u.includes('/c1/'))).toHaveLength(3));
    expect(urls().some((u) => u.includes('/meshes?radius=5000&part=pair'))).toBe(true);
    expect(urls().some((u) => u.includes('/meshes?radius=5000&part=context'))).toBe(true);

    await waitFor(() => expect(screen.getByText('сцена').getAttribute('data-meshes')).toBe('0,1,2'));
    expect(screen.queryByText('загружается окружение…')).toBeNull();
  });
});

describe('Hit scene: your element', () => {
  it('HitScene_MineSelectedAndLabelled', async () => {
    renderUi(<HitScene runId="" number={1} collisionId="c1" mine="B" />, {
      '/api/clash/collisions/c1/scene': scene,
      '/api/clash/collisions/c1/meshes': new Uint32Array([0x31564354, 0]).buffer,
    });

    expect(await screen.findByText('Элемент B (ваш): Стены · Стена · ID 1001')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Свойства' }));
    expect((await screen.findAllByText('ваш')).length).toBeGreaterThan(0);
  });

  it('MySide_ByUserDisciplines', () => {
    expect(mySide({ disciplineA: 'КР', disciplineB: 'ОВК' }, ['овк'])).toBe('B');
    expect(mySide({ disciplineA: 'КР', disciplineB: 'ОВК' }, ['КР'])).toBe('A');
    expect(mySide({ disciplineA: 'ОВК', disciplineB: 'ОВК' }, ['ОВК'])).toBeNull();
    expect(mySide({ disciplineA: 'КР', disciplineB: 'АР' }, ['ОВК'])).toBeNull();
  });
});
