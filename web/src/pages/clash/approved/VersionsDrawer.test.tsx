import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderUi } from '../../../test/renderApp';
import { VersionsDrawer } from './VersionsDrawer';

const versions = [
  { version: 2, approvedAt: '2026-11-02T10:00:00Z', userName: 'Координатор', comment: 'допуск', runId: null },
  { version: 1, approvedAt: '2026-11-01T10:00:00Z', userName: 'Координатор', comment: null, runId: null },
];

describe('Versions drawer', () => {
  it('Versions_ShowChanges', async () => {
    renderUi(<VersionsDrawer partId="p1" versions={versions} onClose={() => {}} />, {
      '/api/clash/parts/p1/versions/2/changes': [
        { kind: 'SetAdded', text: 'Набор «Трубы» (ВК)' },
        { kind: 'ExclusionRemoved', text: 'Исключение «Стены × Двери»' },
        { kind: 'CellTolerance', text: 'АР × ОВ: допуск 20 → 35 мм' },
      ],
    });

    fireEvent.click((await screen.findAllByRole('button', { name: 'Что изменилось' }))[0]);

    expect(await screen.findByText('+ Набор «Трубы» (ВК)')).toBeTruthy();
    expect(screen.getByText('− Исключение «Стены × Двери»')).toBeTruthy();
    expect(screen.getByText('~ АР × ОВ: допуск 20 → 35 мм')).toBeTruthy();
  });
});
