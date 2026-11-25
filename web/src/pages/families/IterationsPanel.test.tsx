import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { FamilyCardView, FamilyIterationView, FamilyVersionView } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: [] };
const coordinator = { ...designer, id: 'u4', role: 'Operator' };

const version = (patch: Partial<FamilyVersionView>): FamilyVersionView => ({
  id: 'v', hash: 'h', revitYear: 2022, state: 'Approved', note: '', relativePath: '', seenAt: '2026-11-20T10:00:00Z', readState: 'Done', readError: null,
  hasPreview: false, isCurrent: false, authorName: null, comment: null, iterationId: null, silent: false, ...patch,
});

const iteration = (patch: Partial<FamilyIterationView> = {}): FamilyIterationView => ({
  id: 'i1', name: 'ЖК Полковая', comment: 'дверь под Полковую', currentVersionId: 'iv2', relativePath: '_Итерации\\Дверь\\ЖК Полковая\\Дверь.rfa',
  fullPath: '\\\\nas\\Families\\_Итерации\\Дверь\\ЖК Полковая\\Дверь.rfa', coordinatorsOnly: false, isFinal: false, finalVersionId: null, finalComment: null,
  finalByName: null, finalAt: null, removedAt: null, createdAt: '2026-11-19T10:00:00Z', updatedAt: '2026-11-21T10:00:00Z', revitYear: 2022, currentHash: 'b',
  previewHash: null, currentReadState: 'Done', content: null, ...patch,
});

const card = (patch: Partial<FamilyCardView> = {}): FamilyCardView => ({
  id: 'c1', name: 'Дверь', relativePath: 'АР\\Дверь.rfa', fullPath: '\\\\nas\\Families\\АР\\Дверь.rfa', section: 'АР', state: 'Approved', category: 'Двери',
  placement: 'OneLevelBasedHosted', revitYear: 2022, typeCount: 0, description: null, tags: [], manufacturer: null, createdAt: '2026-11-19T10:00:00Z',
  updatedAt: '2026-11-20T10:00:00Z', archivedAt: null, currentVersionId: 'v1', currentReadState: 'Done', currentReadError: null, hasPreview: false, content: null,
  versions: [
    version({ id: 'iv2', hash: 'b', iterationId: 'i1', comment: 'ручка слева', seenAt: '2026-11-21T10:00:00Z' }),
    version({ id: 'iv1', hash: 'a', iterationId: 'i1', comment: 'от основной', seenAt: '2026-11-20T11:00:00Z' }),
    version({ id: 'v1', hash: 'a', isCurrent: true, comment: 'общая дверь' }),
  ],
  loadCount: 0, lastLoadedAt: null, openFixRequests: [], loadedProjects: [], coordinatorsOnly: false, silent: false, iterations: [iteration()],
  ...patch,
});

function render(view: FamilyCardView, user: object) {
  const calls: { method: string; url: string; body: unknown }[] = [];
  const record = (method: string) => (url: string, init?: RequestInit) => {
    calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : null });
    return {};
  };
  renderUi(<AuthProvider><FamilyCardDrawer cardId="c1" unreviewed={false} onClose={() => {}} /></AuthProvider>, {
    '/api/auth/me': user,
    '/api/families/c1': view,
    '/api/families/actions': { available: true, reason: null },
    'POST /api/families/iterations/i1/final': record('POST'),
    'PUT /api/families/c1/flags': record('PUT'),
    'POST /api/families/c1/iterations': record('POST'),
  });
  return calls;
}

const openIterations = async () => fireEvent.click(await screen.findByRole('tab', { name: /Итерации/ }));

describe('IterationsPanel', () => {
  it('lists iterations with comment and own versions; base versions tab has only the base', async () => {
    render(card(), designer);

    fireEvent.click(await screen.findByRole('tab', { name: 'Версии' }));
    expect(screen.getByText('общая дверь')).toBeTruthy();
    expect(screen.queryByText('ручка слева')).toBeNull();

    await openIterations();
    const block = screen.getByTestId('iteration-ЖК Полковая');
    expect(within(block).getByText('дверь под Полковую')).toBeTruthy();
    fireEvent.click(within(block).getByRole('button', { name: /Версии итерации/ }));
    expect(within(block).getByText('ручка слева')).toBeTruthy();
    expect(within(block).getByText('от основной')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Новая итерация' })).toBeNull();
  });

  it('final iteration shows what was done and offers to remove the final mark', async () => {
    render(card({ iterations: [iteration({ isFinal: true, finalVersionId: 'iv2', finalComment: 'сдано, корпус 1', finalByName: 'Петров' })] }), coordinator);

    await openIterations();
    const block = screen.getByTestId('iteration-ЖК Полковая');
    expect(within(block).getByText('завершена')).toBeTruthy();
    expect(within(block).getByText(/сдано, корпус 1/)).toBeTruthy();
    expect(within(block).getByRole('button', { name: 'Снять финал' })).toBeTruthy();
    expect(within(block).queryByRole('button', { name: 'Финальная версия…' })).toBeNull();
  });

  it('final is not sent without a comment, then is sent with it', async () => {
    const calls = render(card(), coordinator);

    await openIterations();
    fireEvent.click(within(screen.getByTestId('iteration-ЖК Полковая')).getByRole('button', { name: 'Финальная версия…' }));
    const dialog = await screen.findByRole('dialog', { name: /Финальная версия итерации/ });
    const send = within(dialog).getByRole('button', { name: 'Финальная версия' });
    expect(send).toHaveProperty('disabled', true);

    fireEvent.change(within(dialog).getByLabelText(/Что проделано/), { target: { value: 'сдано заказчику' } });
    fireEvent.click(send);

    await waitFor(() => expect(calls).toEqual([{ method: 'POST', url: '/api/families/iterations/i1/final', body: { versionId: 'iv2', comment: 'сдано заказчику' } }]));
  });

  it('coordinator switches family flags', async () => {
    const calls = render(card(), coordinator);

    fireEvent.click(await screen.findByRole('checkbox', { name: /Без оповещений/ }));

    await waitFor(() => expect(calls).toEqual([{ method: 'PUT', url: '/api/families/c1/flags', body: { coordinatorsOnly: false, silent: true } }]));
  });

  it('coordinator creates an iteration from a chosen version', async () => {
    const calls = render(card(), coordinator);

    await openIterations();
    fireEvent.click(screen.getByRole('button', { name: 'Новая итерация' }));
    const dialog = await screen.findByRole('dialog', { name: /Новая итерация — Дверь/ });
    fireEvent.change(within(dialog).getByLabelText(/Название/), { target: { value: ' Север К2 ' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Новая итерация' }));

    await waitFor(() => expect(calls).toEqual([{
      method: 'POST', url: '/api/families/c1/iterations', body: { name: 'Север К2', fromVersionId: 'v1', comment: null, coordinatorsOnly: false },
    }]));
  });

  it('designer sees a lock and no path for a coordinator-only family', async () => {
    render(card({ coordinatorsOnly: true, fullPath: '' }), designer);

    expect(await screen.findByText(/Только координаторы/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Скопировать путь' })).toBeNull();
  });
});
