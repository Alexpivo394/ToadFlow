import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { configuration, draft, partResponses } from '../../../test/clashPart';
import { pickOption, renderApp } from '../../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

async function savedConfig() {
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/draft' && init?.method === 'PUT')).toBe(true));
  const [, init] = calls().find(([url, i]) => url === '/api/clash/parts/p1/draft' && i?.method === 'PUT')!;
  return JSON.parse(String(init!.body)).configuration;
}

const vk = { id: 'g-vk', name: 'ВК', source: { disciplines: ['ВК'], modelKeys: [] } };
const three = { ...partResponses, '/api/clash/parts/p1/draft': draft(1, { ...configuration, groups: [...configuration.groups, vk] }) };

/** Щелчок кистью: нажать и отпустить на клетке. */
function paint(cell: HTMLElement) {
  fireEvent.mouseDown(cell);
  fireEvent.mouseUp(cell);
}

describe('Matrix tab', () => {
  it('Matrix_UpperTriangleOnly', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    expect(await screen.findByRole('button', { name: 'АР × ОВ' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'АР × АР' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'ОВ × ВК' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'ОВ × АР' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'ВК × АР' })).toBeNull();
  });

  it('Matrix_ClickPaintsWithBrush_ClickAgainTurnsOff', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    fireEvent.click(await screen.findByRole('radio', { name: '50 мм' }));
    paint(screen.getByRole('button', { name: 'ОВ × ВК' }));
    paint(screen.getByRole('button', { name: 'ВК × ВК' }));
    paint(screen.getByRole('button', { name: 'ВК × ВК' }));

    const config = await savedConfig();
    expect(config.matrix).toEqual([
      { groupA: 'g-ar', groupB: 'g-ov', enabled: true, toleranceMm: 20 },
      { groupA: 'g-ov', groupB: 'g-vk', enabled: true, toleranceMm: 50 },
      { groupA: 'g-vk', groupB: 'g-vk', enabled: false, toleranceMm: 50 },
    ]);
  });

  it('Matrix_DragPaintsCells', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    fireEvent.click(await screen.findByRole('radio', { name: 'Не проверять' }));
    fireEvent.mouseDown(screen.getByRole('button', { name: 'АР × ОВ' }));
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'АР × ВК' }));
    fireEvent.mouseUp(screen.getByRole('button', { name: 'АР × ВК' }));
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'ВК × ВК' })); // после отпускания не красит

    const config = await savedConfig();
    expect(config.matrix).toEqual([
      { groupA: 'g-ar', groupB: 'g-ov', enabled: false, toleranceMm: 20 },
      { groupA: 'g-ar', groupB: 'g-vk', enabled: false, toleranceMm: 20 },
    ]);
  });

  it('Matrix_CustomBrush_HeaderPaintsRow', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    fireEvent.change(await screen.findByLabelText('Свой допуск'), { target: { value: '35' } });
    fireEvent.click(screen.getByRole('button', { name: 'Строка ОВ' }));

    const config = await savedConfig();
    expect(config.matrix).toEqual([
      { groupA: 'g-ar', groupB: 'g-ov', enabled: true, toleranceMm: 20 },
      { groupA: 'g-ov', groupB: 'g-ov', enabled: true, toleranceMm: 35 },
      { groupA: 'g-ov', groupB: 'g-vk', enabled: true, toleranceMm: 35 },
    ]);
  });

  it('Matrix_AllTo20_OverwritesEveryCell', async () => {
    const custom = { ...configuration, groups: [...configuration.groups, vk],
      matrix: [{ groupA: 'g-ar', groupB: 'g-ov', enabled: false, toleranceMm: 50 }, { groupA: 'g-vk', groupB: 'g-vk', enabled: true, toleranceMm: 100 }] };
    renderApp('/clash/setup/parts/p1?tab=matrix', { ...partResponses, '/api/clash/parts/p1/draft': draft(1, custom) });

    fireEvent.click(await screen.findByRole('button', { name: 'Все клетки — 20 мм' }));

    const config = await savedConfig();
    expect(config.matrix).toHaveLength(6);
    expect(config.matrix.every((c: { enabled: boolean; toleranceMm: number }) => c.enabled && c.toleranceMm === 20)).toBe(true);
  });

  it('Exclusions_SetsGroupedByGroup', async () => {
    renderApp('/clash/setup/parts/p1?tab=exclusions', partResponses);

    fireEvent.click(await screen.findByRole('button', { name: '+ Исключение' }));
    fireEvent.click(screen.getAllByLabelText('Набор B')[0]);

    expect((await screen.findAllByText('ОВ', { selector: '[class*=groupLabel]' })).length).toBeGreaterThan(0);
    const input = (label: string) => screen.getAllByLabelText(label).filter((e) => e.tagName === 'INPUT')[0];
    await pickOption(input('Набор A'), 'Стены');
    await pickOption(input('Набор B'), 'Воздуховоды');
    const config = await savedConfig();
    expect(config.exclusions).toEqual([{ setA: 's-walls', setB: 's-ducts' }]);
  });
});

vi.setConfig({ testTimeout: 15_000 });
