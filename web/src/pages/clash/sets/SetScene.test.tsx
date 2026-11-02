import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { partResponses } from '../../../test/clashPart';
import { renderApp } from '../../../test/renderApp';

// WebGL в jsdom нет: вместо three.js — список элементов сцены кнопками, щелчок — выбор элемента.
vi.mock('../viewer/ClashViewer', () => ({
  default: (props: { scene: { elements: { index: number; uniqueId: string; role: string }[] }; onPick: (i: number) => void }) => (
    <div>
      {props.scene.elements.map((e) => (
        <button key={e.index} type="button" onClick={() => props.onPick(e.index)}>3D {e.role} {e.uniqueId}</button>
      ))}
    </div>
  ),
}));

vi.setConfig({ testTimeout: 15_000 });

const element = (index: number, uniqueId: string, role = 'set') => ({
  index, role, modelId: 'm1', modelName: 'POL_K1_AR.rvt', discipline: 'АР', uniqueId, elementId: 100 + index, category: 'OST_Walls',
  family: 'Стена', type: '200', level: 'Этаж 1', min: [0, 0, 0], max: [1, 1, 1], triangles: 12, mesh: true,
});

const scene = (extra: object = {}) => ({
  runId: '00000000-0000-0000-0000-000000000000', number: 0, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 0,
  elements: [element(0, 'u1'), element(1, 'u2'), element(2, 'c1', 'context')], meshTriangles: 36, truncated: false, total: 2, ...extra,
});

const responses = (extra: Record<string, unknown> = {}) => ({
  ...partResponses,
  '/api/clash/parts/p1/preview': {
    matched: 2, total: 10, byAnyOf: [2],
    sample: [
      { modelKey: 'm1', uniqueId: 'u1', elementId: 100, category: 'OST_Walls', family: 'Стена', type: '200' },
      { modelKey: 'm1', uniqueId: 'u2', elementId: 101, category: 'OST_Walls', family: 'Стена', type: '200' },
    ],
  },
  '/api/clash/parts/p1/draft/sets/scene': scene(),
  '/api/clash/parts/p1/draft/sets/meshes': new Uint32Array([0x31564354, 0]).buffer,
  '/api/clash/parts/p1/draft/sets/why': ['Категория равно «Стены»'],
  ...extra,
});

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const sceneBodies = () => (fetch as unknown as FetchMock).mock.calls
  .filter(([url, init]) => url === '/api/clash/parts/p1/draft/sets/scene' && init?.method === 'POST')
  .map(([, init]) => JSON.parse(String(init!.body)));

describe('Set in 3D', () => {
  it('SetScene_OpensWithFocus', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', responses());

    const rowButtons = await screen.findAllByRole('button', { name: /^3D образца/ });
    fireEvent.click(rowButtons[1]);

    await waitFor(() => expect(sceneBodies()).toHaveLength(1));
    const body = sceneBodies()[0];
    expect(body.setId).toBe('s-walls');
    expect(body.focus).toEqual({ model: 'm1', uniqueId: 'u2' });
    expect(body.configuration.sets.map((s: { id: string }) => s.id)).toContain('s-walls');
    expect(await screen.findByText('Почему в наборе')).toBeTruthy(); // образец выбран сразу
  });

  it('SetScene_TruncatedCaption', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', responses({ '/api/clash/parts/p1/draft/sets/scene': scene({ truncated: true, total: 4321 }) }));

    fireEvent.click(await screen.findByRole('button', { name: 'Показать набор в 3D' }));

    expect(await screen.findByText(/^Показано 2 из 4\s321$/)).toBeTruthy();
    expect(sceneBodies()[0].focus ?? null).toBeNull();
  });

  it('SetScene_ClickShowsWhy', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', responses());
    fireEvent.click(await screen.findByRole('button', { name: 'Показать набор в 3D' }));

    fireEvent.click(await screen.findByRole('button', { name: '3D set u1' }));

    expect(await screen.findByText('Категория равно «Стены»')).toBeTruthy();
    const why = (fetch as unknown as FetchMock).mock.calls.find(([url]) => url === '/api/clash/parts/p1/draft/sets/why')!;
    expect(JSON.parse(String(why[1]!.body)).element).toEqual({ model: 'm1', uniqueId: 'u1' });
  });

  it('SetScene_Refresh_SameConfig_Refetches', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', responses());
    fireEvent.click(await screen.findByRole('button', { name: 'Показать набор в 3D' }));
    await screen.findByRole('button', { name: '3D set u1' });
    expect(sceneBodies()).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Обновить' }));

    await waitFor(() => expect(sceneBodies()).toHaveLength(2));
  });
});
