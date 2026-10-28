import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const latest = {
  id: 'n2', extractedAt: '2026-10-28T09:00:00Z', revitYear: 2022, viewName: 'Navisworks', elements: 32419, triangles: 960000, inventory: 40211,
  geometryBytes: 12_582_912, parametersBytes: 1_048_576, jobId: 'j2',
};
const older = { ...latest, id: 'n1', extractedAt: '2026-10-21T09:00:00Z', elements: 32000, jobId: 'j1' };
const catalog = {
  categories: [{ key: 'OST_PipeCurves', count: 1200 }, { key: 'OST_Walls', count: 3400 }],
  parameters: [
    { name: 'ADSK_Марка', sharedGuid: '2204049c-d557-4dfc-8d70-13f19715e46d', count: 4000, isNumeric: false, categories: ['OST_Walls'] },
    { name: 'Диаметр', sharedGuid: null, count: 1200, isNumeric: true, categories: ['OST_PipeCurves'] },
    { name: 'Комментарии', sharedGuid: null, count: 4600, isNumeric: false, categories: ['OST_PipeCurves', 'OST_Walls'] },
  ],
  families: ['Стена'], types: ['200'], levels: ['Этаж 1'], worksets: ['АР'],
};
const responses = {
  '/api/clash/models': [
    { id: 'm1', name: 'АР_К1.rvt', modelPath: 'RSN://rs22/ЖК/АР_К1.rvt', latest, snapshotCount: 2 },
    { id: 'm2', name: 'ОВ_К1.rvt', modelPath: 'RSN://rs22/ЖК/ОВ_К1.rvt', latest: null, snapshotCount: 0 },
  ],
  '/api/clash/models/m1/snapshots': [latest, older],
  '/api/clash/catalog': catalog,
};

describe('ClashPage', () => {
  it('ClashPage_ShowsModelsAndLatestSnapshot', async () => {
    renderApp('/clash/snapshots', responses);

    expect(await screen.findByText('RSN://rs22/ЖК/АР_К1.rvt')).toBeTruthy();
    expect(screen.getByText('32 419')).toBeTruthy();
    expect(screen.getByText('13 МБ')).toBeTruthy(); // геометрия и параметры
    expect(screen.getByText('ещё не выгружалась')).toBeTruthy();
    fireEvent.click(screen.getByText('RSN://rs22/ЖК/АР_К1.rvt'));
    expect(await screen.findByText('32 000')).toBeTruthy(); // прошлая выгрузка в истории
  });

  it('CatalogPanel_FiltersParametersBySearch', async () => {
    renderApp('/clash/snapshots', responses);

    fireEvent.click(await screen.findByRole('tab', { name: 'Параметры' }));
    expect(await screen.findByText('Диаметр')).toBeTruthy();
    fireEvent.change(screen.getByPlaceholderText('Поиск параметра'), { target: { value: 'adsk' } });

    expect(screen.getByText('ADSK_Марка')).toBeTruthy();
    expect(screen.queryByText('Диаметр')).toBeNull();
    expect(screen.queryByText('Комментарии')).toBeNull();
  });
});
