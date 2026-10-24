import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../../test/renderApp';

const ready = { id: 'p1', name: 'ОВ', ruleCount: 2, viewName: 'Navisworks', worksetKeyword: '', updatedAt: '2026-10-24T10:00:00Z', updatedBy: 'А. Борисов', problems: [] };
const draft = { ...ready, id: 'p2', name: 'Черновик', ruleCount: 0, viewName: '', problems: ['«Черновик»: нет правил.'] };

const responses = {
  '/api/loi/profiles': [ready, draft],
  '/api/loi/catalog': { catalogs: [{ revitYear: 2024, updatedAt: '2026-10-24T09:00:00Z', categories: 812, parameters: 3456, hasSharedParameterFile: false }], agentYears: [2024, 2022] },
  '/api/loi/catalog/2024': {
    revitYear: 2024,
    updatedAt: '2026-10-24T09:00:00Z',
    catalog: { categories: [{ key: 'OST_DuctCurves', name: 'Воздуховоды', isModel: true }], parameters: ['ADSK_Марка'], hasSharedParameterFile: false },
  },
  '/api/loi/profiles/p1': {
    id: 'p1', name: 'ОВ', updatedAt: ready.updatedAt, updatedBy: ready.updatedBy, problems: [],
    profile: { Name: 'ОВ', ViewName: 'Navisworks', WorksetKeyword: '', Rules: [{ Title: 'Воздуховоды', Filter: { Categories: ['OST_DuctCurves', 'OST_Old'] }, Parameters: ['ADSK_Марка'] }] },
  },
};

describe('LoiSettings', () => {
  it('lists profiles, readiness and the Revit catalog', async () => {
    renderApp('/settings/loi', responses);

    expect(await screen.findByText('ОВ')).toBeTruthy();
    expect(screen.getByText('готов')).toBeTruthy();
    expect(screen.getByText('не готов')).toBeTruthy();
    expect(screen.getAllByText('Revit 2024').length).toBeGreaterThan(0);
    expect(screen.getByText('без ФОП')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Загрузить из ParamChecker' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Обновить из Revit' })).toBeTruthy();
  });

  it('opens a profile with category names from the catalog', async () => {
    renderApp('/settings/loi', responses);

    fireEvent.click(await screen.findByText('ОВ'));

    expect(await screen.findByText('Профиль «ОВ»')).toBeTruthy();
    expect(screen.getByDisplayValue('Воздуховоды')).toBeTruthy(); // название правила
    expect(screen.getAllByText('OST_Old').length).toBeGreaterThan(0); // нет в каталоге — остаётся выбранной под ключом
    expect(screen.getByRole('button', { name: '+ Правило' })).toBeTruthy();
  });

  it('viewer sees profiles but cannot change them', async () => {
    renderApp('/settings/loi', { ...responses, '/api/auth/me': { id: 'u3', login: 'gip', displayName: 'ГИП', role: 'Viewer', isDisabled: false, lastLoginAt: null } });

    expect(await screen.findByText('ОВ')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '+ Новый профиль' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Обновить из Revit' })).toBeNull();
  });
});
