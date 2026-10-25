import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const model = {
  modelKey: 'rsn://rs22/pol/pol_ar.rvt', modelName: 'POL_AR_RLP_S3_R22', modelPath: 'RSN://rs22/POL/POL_AR_RLP_S3_R22.rvt', customer: 'ГК Пионер',
  object: 'Полковая', discipline: 'АР', part: 'секция 3', checkedAt: '2026-10-24T10:00:00Z', source: 'Schedule', profileName: 'АР', elements: 120,
  fillRate: 0.9, completeness: 0.8, previousFillRate: 0.5, profileChanged: true,
};
const unknown = { ...model, modelKey: 'x', modelName: 'XXX_KR', object: null, discipline: 'КР', fillRate: 0.4, previousFillRate: null, profileChanged: false };

const responses = {
  '/api/loi/analytics/settings': { enabled: true, retentionYears: 0 },
  '/api/loi/analytics/filters': { customers: ['ГК Пионер'], objects: [{ name: 'Полковая', customer: 'ГК Пионер' }], disciplines: ['АР', 'КР'], parts: [], profiles: ['АР'], first: null, last: null },
  '/api/loi/analytics/overview': {
    models: 2, checks: 3, manualChecks: 1, fillRate: 0.65, completeness: 0.6,
    byObject: [{ key: 'Полковая', customer: 'ГК Пионер', models: 1, fillRate: 0.9, completeness: 0.8 }, { key: null, customer: null, models: 1, fillRate: 0.4, completeness: 0.4 }],
    byDiscipline: [{ key: 'АР', customer: null, models: 1, fillRate: 0.9, completeness: 0.8 }],
    matrix: [{ object: 'Полковая', discipline: 'АР', models: 1, fillRate: 0.9 }, { object: null, discipline: 'КР', models: 1, fillRate: 0.4 }],
    granularity: 'day', trend: [{ bucket: '2026-10-24', series: null, checks: 3, fillRate: 0.65, completeness: 0.6 }],
    trendByObject: [{ bucket: '2026-10-24', series: 'Полковая', checks: 2, fillRate: 0.9, completeness: 0.8 }],
    worstModels: [unknown, model],
    worstParameters: [{ name: 'ADSK_Марка', models: 2, total: 100, filled: 40, empty: 50, missing: 10, fillRate: 0.4, worstModels: ['XXX_KR'] }],
  },
  '/api/loi/analytics/models': [model, unknown],
  '/api/loi/analytics/model': {
    model,
    checks: [
      { id: 'c1', checkedAt: '2026-10-20T10:00:00Z', source: 'Manual', userName: 'И. Петров', profileName: 'АР', profileFingerprint: 'v1', elements: 120, fillRate: 0.5, completeness: 0.4 },
      { id: 'c2', checkedAt: '2026-10-24T10:00:00Z', source: 'Schedule', userName: null, profileName: 'АР', profileFingerprint: 'v2', elements: 120, fillRate: 0.9, completeness: 0.8 },
    ],
    rules: [{ checkId: 'c2', position: 0, title: 'Стены', elements: 120, fillRate: 0.9, completeness: 0.8 }],
    parameters: [{ rule: 'Стены', name: 'ADSK_Марка', filled: 90, empty: 8, missing: 2, fillRate: 0.9 }],
  },
};

describe('AnalyticsPage', () => {
  it('overview: shares, unrecognized objects and the worst', async () => {
    renderApp('/analytics', responses);

    await screen.findByText('Отстающие модели', {}, { timeout: 5000 }); // графики в jsdom рисуются долго
    expect(await screen.findByText('65,0 %')).toBeTruthy();
    expect(screen.getByText('из них вручную — 1')).toBeTruthy();
    expect(screen.getAllByText('не распознано').length).toBeGreaterThan(0);
    expect(screen.getAllByText('ADSK_Марка').length).toBeGreaterThan(0);
    expect(screen.getAllByText('POL_AR_RLP_S3_R22').length).toBeGreaterThan(0);
  });

  it('models: change since the previous check is marked when rules changed', async () => {
    renderApp('/analytics/models', responses);

    expect(await screen.findByText('Модели (2)')).toBeTruthy();
    expect(screen.getByText('▲ 40 *')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Выгрузить в Excel (CSV)' })).toBeTruthy();
  });

  it('model history', async () => {
    renderApp('/analytics/model?key=rsn%3A%2F%2Frs22%2Fpol%2Fpol_ar.rvt', responses);

    expect(await screen.findByText('Правила в последней проверке')).toBeTruthy();
    expect(screen.getByText(/правила профиля менялись 1/)).toBeTruthy();
    expect(screen.getByText('вручную · И. Петров')).toBeTruthy();
  });

  it('disabled analytics is announced', async () => {
    renderApp('/analytics', { ...responses, '/api/loi/analytics/settings': { enabled: false, retentionYears: 0 } });

    expect(await screen.findByText(/Аналитика выключена/)).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: 'Параметры' }));
  });
});
