import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { describe, expect, it, vi } from 'vitest';
import type { CollisionFacets } from '../../api/types';
import { theme } from '../../theme';
import { ListFilters } from './ListFilters';
import type { ListParams } from './useListParams';

const params: ListParams = { status: 'open', sort: 'depth', group: 'none', page: 1 };
const facets: CollisionFacets = {
  pairs: [{ value: 'КР × ОВК', count: 131 }, { value: 'АР × ВК', count: 1204 }],
  levels: [{ value: 'Этаж 1', count: 10 }],
  models: [],
  categories: [],
};

function renderFilters(onChange = vi.fn(), onOpen = vi.fn()) {
  render(
    <MantineProvider theme={theme}>
      <ListFilters params={params} facets={facets} fields={['q', 'status', 'pair', 'level']} onChange={onChange} onOpen={onOpen} />
    </MantineProvider>,
  );
  return { onChange, onOpen };
}

describe('ListFilters', () => {
  it('ListFilters_PairOptionsFromFacets', async () => {
    const { onChange } = renderFilters();
    fireEvent.click(screen.getAllByLabelText('Пара разделов')[0]);
    // Числа — toLocaleString('ru-RU'): разряды через неразрывный пробел.
    expect(await screen.findByRole('option', { name: /^Все пары \(1\s335\)$/ })).toBeTruthy();
    fireEvent.click(await screen.findByRole('option', { name: /^АР × ВК — 1\s204$/ }));
    expect(onChange).toHaveBeenCalledWith({ pair: 'АР × ВК' });
  });

  it('ListFilters_PasteClashText_Opens', () => {
    const { onOpen, onChange } = renderFilters();
    const search = screen.getByRole('textbox', { name: 'Поиск' });
    fireEvent.paste(search, { clipboardData: { getData: () => 'ToadClash №123 — Корпус 1\nhttps://x/clash/checks/p1?collision=abc' } });
    expect(onOpen).toHaveBeenCalledWith('abc');

    fireEvent.paste(search, { clipboardData: { getData: () => 'ToadClash №77' } });
    expect(onOpen).toHaveBeenLastCalledWith(77);
    expect(onChange).not.toHaveBeenCalled();
  });
});
