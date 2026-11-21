import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import { AnalyticsPage } from './AnalyticsPage';
import { DirectionsPanel } from './DirectionsPanel';
import { MissesPage } from './MissesPage';

vi.setConfig({ testTimeout: 20_000 });

const admin = { id: 'u1', login: 'admin', displayName: 'Администратор', role: 'Admin', isDisabled: false, lastLoginAt: null, disciplines: [] };

describe('families insights', () => {
  it('a miss becomes a request in one click; an open request is linked', async () => {
    renderUi(
      <AuthProvider>
        <Routes>
          <Route path="/families/misses" element={<MissesPage />} />
          <Route path="/families/requests/new" element={<p>форма заявки</p>} />
        </Routes>
      </AuthProvider>,
      {
        '/api/families/misses': [
          { query: 'задвижка шиберная', count: 4, users: 3, lastAt: '2026-11-21T10:00:00Z', requestId: 'r5', requestNumber: 5 },
          { query: 'отвод 45', count: 2, users: 1, lastAt: '2026-11-21T09:00:00Z', requestId: null, requestNumber: null },
        ],
      }, '/families/misses');

    expect(await screen.findByText('задвижка шиберная')).toBeTruthy();
    expect(screen.getByRole('link', { name: '№ 5' }).getAttribute('href')).toBe('/families/requests/r5');
    fireEvent.click(screen.getByRole('button', { name: 'Создать заявку: отвод 45' }));
    expect(await screen.findByText('форма заявки')).toBeTruthy();
  });

  it('analytics shows directions and downloads Excel', async () => {
    let downloaded = false;
    vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:x', revokeObjectURL: () => undefined }));
    renderUi(<AuthProvider><AnalyticsPage /></AuthProvider>, {
      '/api/families/analytics': {
        weeks: [{ week: '2026-11-16', created: 3, closed: 2 }],
        byDirection: [{ name: 'ОВ/ВК', open: 4, overdue: 1, accepted: 6, medianHours: 30, declinedShare: 0.17 }],
        byAssignee: [{ name: 'Петров', open: 3 }],
        topLoaded: [{ cardId: 'c1', name: 'Дверь', section: 'АР', loads: 40 }],
        unused: [{ cardId: 'c2', name: 'Окно старое', section: 'АР', loads: 0 }],
        topMisses: [],
      },
      '/api/families/analytics.xlsx': () => { downloaded = true; return new ArrayBuffer(2); },
    });

    expect(await screen.findByText('ОВ/ВК')).toBeTruthy();
    expect(screen.getByText('17%')).toBeTruthy();
    expect(screen.getByText('Окно старое')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Скачать Excel' }));
    await waitFor(() => expect(downloaded).toBe(true));
  });

  it('an admin adds a direction and sees uncovered sections', async () => {
    let saved: unknown = null;
    renderUi(<AuthProvider><DirectionsPanel /></AuthProvider>, {
      '/api/auth/me': admin,
      '/api/families/directions': [{ id: 'd1', name: 'ОВ/ВК', sections: ['ОВ'], disciplines: ['ОВ'], coordinators: [], hint: '' }],
      '/api/families/directions/uncovered': ['Двери'],
      '/api/families/facets': { sections: [{ value: 'ОВ', count: 1 }, { value: 'Двери', count: 2 }], categories: [], years: [], placements: [] },
      '/api/auth/disciplines': ['ОВ', 'АР'],
      '/api/families/requests/assignees': [{ id: 'u3', name: 'Петров' }],
      'PUT /api/families/directions': (_: string, init?: RequestInit) => { saved = JSON.parse(String(init?.body)); return []; },
    });

    expect((await screen.findByText(/Разделы вне направлений/)).textContent).toContain('Двери');
    fireEvent.click(screen.getByRole('button', { name: 'Добавить направление' }));
    await waitFor(() => expect(screen.getAllByLabelText('Название')).toHaveLength(2));
    fireEvent.change(screen.getAllByLabelText('Название')[1], { target: { value: 'АР/КР' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить направления' }));

    await waitFor(() => expect(saved).toEqual([
      { name: 'ОВ/ВК', sections: ['ОВ'], disciplines: ['ОВ'], coordinators: [], hint: '' },
      { name: 'АР/КР', sections: [], disciplines: [], coordinators: [], hint: '' },
    ]));
  });
});
