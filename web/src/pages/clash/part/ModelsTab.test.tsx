import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ClashPartView } from '../../../api/types';
import { renderUi } from '../../../test/renderApp';
import { ModelsTab } from './ModelsTab';
type FetchMock = { mock: { calls: [string, RequestInit?][] } };const calls = () => (fetch as unknown as FetchMock).mock.calls;

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

  it('PartModels_RemoveOne_SavesRestAndWarnsAboutGroup', async () => {
    const two: ClashPartView = {
      ...part(false),
      models: [
        { modelId: 'm1', name: 'POL_K1_AR.rvt', modelPath: '', parsedDiscipline: 'АР', disciplineOverride: null, discipline: 'АР', latest: null },
        { modelId: 'm2', name: 'POL_K1_ITP.rvt', modelPath: '', parsedDiscipline: null, disciplineOverride: 'ИТП', discipline: 'ИТП', latest: null },
      ],
    };
    renderUi(<ModelsTab part={two} readOnly={false} />, { 'PUT /api/clash/parts/p1/models': two });

    fireEvent.click(screen.getAllByRole('button', { name: 'Убрать из части' })[1]);
    expect(await screen.findByText(/уйдут группа ИТП, её наборы/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Да' }));

    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/models' && init?.method === 'PUT')).toBe(true));
    const [, init] = calls().find(([url, i]) => url === '/api/clash/parts/p1/models' && i?.method === 'PUT')!;
    expect(JSON.parse(String(init!.body))).toEqual([{ modelId: 'm1', disciplineOverride: null }]);
  });
});
