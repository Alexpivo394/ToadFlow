import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { configuration, draft, partResponses } from '../../../test/clashPart';
import { renderApp } from '../../../test/renderApp';

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

describe('Matrix tab', () => {
  it('Matrix_UpperTriangleOnly', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    expect(await screen.findByRole('button', { name: 'АР × ОВ' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'АР × АР' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'ОВ × ВК' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'ОВ × АР' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'ВК × АР' })).toBeNull();
  });

  it('Matrix_ClickToggles', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    fireEvent.click(await screen.findByRole('button', { name: 'АР × ОВ' }));
    fireEvent.click(screen.getByRole('button', { name: 'ВК × ВК' }));

    const config = await savedConfig();
    expect(config.matrix).toEqual([
      { groupA: 'g-ar', groupB: 'g-ov', enabled: false, toleranceMm: 20 },
      { groupA: 'g-vk', groupB: 'g-vk', enabled: true, toleranceMm: 20 },
    ]);
  });

  it('Matrix_ShiftSelectAndSetTolerance', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    fireEvent.mouseDown(await screen.findByRole('button', { name: 'АР × ОВ' }), { shiftKey: true });
    fireEvent.mouseDown(screen.getByRole('button', { name: 'ОВ × ВК' }), { shiftKey: true });
    expect(screen.getByText('Выбрано клеток: 2')).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Допуск выделенных'), { target: { value: '50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Задать допуск' }));

    const config = await savedConfig();
    expect(config.matrix).toEqual([
      { groupA: 'g-ar', groupB: 'g-ov', enabled: true, toleranceMm: 50 },
      { groupA: 'g-ov', groupB: 'g-vk', enabled: true, toleranceMm: 50 },
    ]);
  });

  it('Matrix_HeaderSelectsRow', async () => {
    renderApp('/clash/setup/parts/p1?tab=matrix', three);

    fireEvent.click(await screen.findByRole('button', { name: 'Строка ОВ' }));

    expect(screen.getByText('Выбрано клеток: 2')).toBeTruthy(); // ОВ × ОВ и ОВ × ВК
  });

  it('Exclusions_SetsGroupedByGroup', async () => {
    renderApp('/clash/setup/parts/p1?tab=exclusions', partResponses);

    fireEvent.click(await screen.findByRole('button', { name: '+ Исключение' }));
    fireEvent.click(screen.getAllByLabelText('Набор B')[0]);

    expect((await screen.findAllByText('ОВ', { selector: '[class*=groupLabel]' })).length).toBeGreaterThan(0);
    const config = await savedConfig();
    expect(config.exclusions).toEqual([{ setA: 's-walls', setB: 's-ducts' }]);
  });
});

vi.setConfig({ testTimeout: 15_000 });
