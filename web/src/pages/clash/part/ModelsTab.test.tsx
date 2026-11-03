import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ClashPartView } from '../../../api/types';
import { renderUi } from '../../../test/renderApp';
import { ModelsTab } from './ModelsTab';

const part = (compositionPending: boolean): ClashPartView => ({
  id: 'p1', name: 'Корпус 1', object: null, part: null, updatedAt: '2026-11-03T10:00:00Z', compositionPending,
  models: [{ modelId: 'm1', name: 'POL_K1_OV.rvt', modelPath: '', parsedDiscipline: 'ОВ', disciplineOverride: 'ВК', discipline: 'ВК', latest: null }],
});

describe('Part models', () => {
  it('PartModels_ChangedAfterApprove_ShowsPendingNote', () => {
    renderUi(<ModelsTab part={part(true)} readOnly={false} />, {});
    expect(screen.getByText('Изменения состава войдут в проверку после утверждения')).toBeTruthy();
  });

  it('PartModels_SameAsApproved_NoNote', () => {
    renderUi(<ModelsTab part={part(false)} readOnly={false} />, {});
    expect(screen.queryByText('Изменения состава войдут в проверку после утверждения')).toBeNull();
  });
});
