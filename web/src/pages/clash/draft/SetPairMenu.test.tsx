import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { configuration, draft, partResponses } from '../../../test/clashPart';
import { pickOption, renderApp } from '../../../test/renderApp';

vi.setConfig({ testTimeout: 15_000 });

// WebGL в jsdom нет: вместо сцены three.js — заглушка с номером коллизии.
vi.mock('../viewer/ClashViewer', () => ({ default: (props: { scene: { number: number } }) => <div>сцена {props.scene.number}</div> }));

const hit = (n: number) => ({
  runId: 'r1', number: n, depthMm: 50, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', setNamesA: ['Стены'], categoryA: 'OST_Walls',
  familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', setNamesB: ['Воздуховоды'],
  categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k-arov'], setPairKey: 'swalls|sducts',
});

const responses = (config: object = configuration) => ({
  ...partResponses,
  '/api/clash/parts/p1/draft': draft(1, config),
  '/api/clash/parts/p1/draft/run': { runId: 'r1', state: 'Succeeded', stage: null, hitCount: 2, error: null, createdAt: '2026-10-28T10:00:00Z', finishedAt: '2026-10-28T10:01:00Z', stats: null },
  '/api/clash/parts/p1/draft/summary': {
    total: 2, legacy: false,
    checks: [{ key: 'k-arov', groupA: 'g-ar', groupAName: 'АР', groupB: 'g-ov', groupBName: 'ОВ', count: 2, toleranceMm: 20 }],
  },
  '/api/clash/parts/p1/draft/hits': { total: 2, items: [hit(1), hit(2)] },
  '/api/clash/parts/p1/draft/facets': { levels: [], models: [], categories: [] },
  '/api/clash/parts/p1/draft/checks/k-arov/setpairs': [{ setA: 's-walls', setAName: 'Стены', setB: 's-ducts', setBName: 'Воздуховоды', count: 2 }],
  '/api/clash/runs/r1/hits/1/scene': { runId: 'r1', number: 1, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [], meshTriangles: 0, truncated: false },
  '/api/clash/runs/r1/hits/1/meshes': new Uint32Array([0x31564354, 0]).buffer,
});

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

const openMenu = async () => {
  fireEvent.click(await screen.findByRole('button', { name: 'Действия с парой «Стены × Воздуховоды»' }, { timeout: 5000 }));
};

describe('Draft noise to rules', () => {
  it('NoiseMenu_Exclude_MarksGroupAndShowsBanner', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', responses());
    await openMenu();

    fireEvent.click(await screen.findByRole('menuitem', { name: 'Не проверять эту пару наборов' }));

    const banner = await screen.findByTestId('unsaved-banner');
    expect(banner.textContent).toContain('Есть несохранённые изменения: не проверять «Стены × Воздуховоды»');
    expect(screen.getByText('будет исключена')).toBeTruthy();
    fireEvent.click(within(banner).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => {
      const put = calls().find(([url, init]) => url === '/api/clash/parts/p1/draft' && init?.method === 'PUT');
      expect(put).toBeTruthy();
      expect(JSON.parse(String(put![1]!.body)).configuration.exclusions).toEqual([{ setA: 's-walls', setB: 's-ducts' }]);
    });
  });

  it('NoiseMenu_PairInTwoChecks_AsksWhichCell', async () => {
    const twoCells = {
      ...configuration,
      matrix: [{ groupA: 'g-ar', groupB: 'g-ov', enabled: true, toleranceMm: 20 }, { groupA: 'g-ov', groupB: 'g-ar', enabled: true, toleranceMm: 50 }],
    };
    renderApp('/clash/setup/parts/p1?tab=draft', responses(twoCells));
    await openMenu();

    fireEvent.click(await screen.findByRole('menuitem', { name: 'Допуск клетки…' }));
    fireEvent.click(await screen.findByLabelText('«ОВ × АР»: 50 мм'));
    fireEvent.change(screen.getByLabelText('Допуск «ОВ × АР», мм'), { target: { value: '35' } });
    fireEvent.click(screen.getByRole('button', { name: 'Применить' }));

    expect((await screen.findByTestId('unsaved-banner')).textContent).toContain('допуск «ОВ × АР» 35 мм');
  });

  it('NoiseMenu_OpenSet_Navigates', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', responses());
    await openMenu();

    fireEvent.click(await screen.findByRole('menuitem', { name: 'Открыть набор «Воздуховоды» (ОВ)' }));

    expect(((await screen.findByLabelText('Имя набора')) as HTMLInputElement).value).toBe('Воздуховоды');
  });
});

describe('Exclusions tab', () => {
  it('Exclusions_AddRow_EmptyNotSaved', async () => {
    renderApp('/clash/setup/parts/p1?tab=exclusions', responses());

    fireEvent.click(await screen.findByRole('button', { name: '+ Исключение' }));

    const a = screen.getAllByLabelText('Набор A').filter((e) => e.tagName === 'INPUT') as HTMLInputElement[];
    expect(a.map((e) => e.value)).toEqual(['']);
    expect(screen.queryByText('● есть несохранённые изменения')).toBeNull();

    await pickOption(a[0], 'Стены');
    expect(screen.queryByText('● есть несохранённые изменения')).toBeNull();
    await pickOption(screen.getAllByLabelText('Набор B').filter((e) => e.tagName === 'INPUT')[0], 'Воздуховоды');

    expect(await screen.findByText('● есть несохранённые изменения')).toBeTruthy();
  });
});
