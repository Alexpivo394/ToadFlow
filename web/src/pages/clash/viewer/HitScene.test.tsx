import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderUi } from '../../../test/renderApp';
import { mySide } from '../my/MyPage';
import { HitScene } from './HitScene';

// WebGL в jsdom нет: вместо сцены three.js — заглушка.
vi.mock('./ClashViewer', () => ({ default: () => <div>сцена</div> }));

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
