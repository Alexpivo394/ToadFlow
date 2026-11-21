import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { RequestListItem } from './api';
import { RequestsPage } from './RequestsPage';

vi.setConfig({ testTimeout: 20_000 });

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: [] };
const coordinator = { id: 'u3', login: 'petrov', displayName: 'Петров', role: 'Operator', isDisabled: false, lastLoginAt: null, disciplines: [] };

const item = (patch: Partial<RequestListItem> = {}): RequestListItem => ({
  id: 'r1', number: 12, title: 'Клапан обратный DN50', section: 'ОВ', urgency: 'Normal', project: 'ЖК Север', state: 'New', authorId: 'u2',
  authorName: 'Иванов', assigneeId: null, assigneeName: null, cardId: null, cardName: null, members: 1, isMember: true, unread: false,
  createdAt: '2026-11-20T10:00:00Z', updatedAt: '2026-11-20T10:00:00Z', kind: 'New', directionId: 'd1', directionName: 'ОВ/ВК', neededBy: null,
  promisedBy: null, blocksIssue: false, overdue: false, turn: 'staff', waitingDays: 2, thumbnailFileId: null, revitYear: null, targetCardId: null,
  targetCardName: null, resolution: null, reviewSince: null, ...patch,
});

const page = (
  <AuthProvider>
    <Routes>
      <Route path="/families/requests" element={<RequestsPage />} />
      <Route path="/families/requests/new" element={<p>форма</p>} />
    </Routes>
  </AuthProvider>
);

describe('requests list', () => {
  it('a designer sees own requests with whose move it is and can file a new one', async () => {
    let asked = '';
    let stats = false;
    renderUi(page, {
      '/api/auth/me': designer,
      '/api/families/requests': (url: string) => { asked = url; return { items: [item({ unread: true, state: 'NeedInfo', turn: 'author' })], total: 1 }; },
      '/api/families/requests/stats': () => { stats = true; return {}; },
    }, '/families/requests');

    expect(await screen.findByText('Клапан обратный DN50')).toBeTruthy();
    expect(asked).toContain('scope=mine');
    expect(screen.getAllByText('ждёт автора').length).toBeGreaterThan(0);
    expect(screen.getByText('Ход автора')).toBeTruthy();
    expect(screen.queryByText('Моё направление')).toBeNull();
    expect(stats).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Новая заявка' }));
    expect(await screen.findByText('форма')).toBeTruthy();
  });

  it('a coordinator starts at the direction queue with numbers that filter', async () => {
    const asked: string[] = [];
    renderUi(page, {
      '/api/auth/me': coordinator,
      '/api/families/requests': (url: string) => {
        asked.push(url);
        return { items: [item({ overdue: true, neededBy: '2026-11-01', blocksIssue: true }), item({ id: 'r2', number: 13, title: 'Решётка' })], total: 2 };
      },
      '/api/families/requests/stats': { open: 7, overdue: 1, unassigned: 3, medianCloseHours: 30, mineDirection: 5 },
    }, '/families/requests');

    expect(await screen.findByText('Клапан обратный DN50')).toBeTruthy();
    expect(asked[0]).toContain('scope=direction');
    expect(screen.getByText('01.11.2026').getAttribute('data-overdue')).toBe('true');
    expect(screen.getByText('просрочено')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /без исполнителя/ }));
    await waitFor(() => expect(asked.at(-1)).toContain('scope=unassigned'));

    fireEvent.click(screen.getByRole('button', { name: /просрочено/ }));
    await waitFor(() => expect(screen.queryByText('Решётка')).toBeNull());
    expect(screen.getByText('Клапан обратный DN50')).toBeTruthy();
  });
});
