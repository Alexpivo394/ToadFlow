import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { RequestListItem, RequestView } from './api';
import { RequestNewPage } from './RequestNewPage';
import { RequestPage } from './RequestPage';
import { RequestsPage } from './RequestsPage';

vi.setConfig({ testTimeout: 15_000 });

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: [] };
const coordinator = { id: 'u3', login: 'petrov', displayName: 'Петров', role: 'Operator', isDisabled: false, lastLoginAt: null, disciplines: [] };

const item = (patch: Partial<RequestListItem> = {}): RequestListItem => ({
  id: 'r1', number: 12, title: 'Клапан обратный DN50', section: 'ОВ', urgency: 'Normal', project: 'Дом на Набережной', state: 'New',
  authorId: 'u2', authorName: 'Иванов', assigneeId: null, assigneeName: null, cardId: null, cardName: null, members: 1, isMember: true, unread: false,
  createdAt: '2026-11-20T10:00:00Z', updatedAt: '2026-11-20T10:00:00Z', ...patch,
});

const view = (patch: Partial<RequestListItem> = {}, extra: Partial<RequestView> = {}): RequestView => ({
  request: item(patch), text: 'межфланцевый', reason: null, members: [{ userId: 'u2', name: 'Иванов' }],
  messages: [{ id: 'm1', authorId: 'u3', authorName: 'Петров', text: 'взял в работу', isEvent: true, at: '2026-11-20T11:00:00Z' }],
  files: [{ id: 'f1', fileName: 'Каталог.pdf', size: 2048, contentType: 'application/pdf', uploadedByName: 'Иванов', at: '2026-11-20T10:05:00Z' }],
  ...extra,
});

const page = (
  <AuthProvider>
    <Routes>
      <Route path="/families/requests" element={<RequestsPage />} />
      <Route path="/families/requests/new" element={<RequestNewPage />} />
      <Route path="/families/requests/:id" element={<RequestPage />} />
    </Routes>
  </AuthProvider>
);

describe('requests', () => {
  it('a designer sees own requests with an unread mark and can file a new one', async () => {
    let asked = '';
    renderUi(page, {
      '/api/auth/me': designer,
      '/api/families/requests': (url: string) => {
        asked = url;
        return { items: [item({ unread: true, state: 'InWork', assigneeName: 'Петров' })], total: 1 };
      },
    }, '/families/requests');

    expect(await screen.findByText('Клапан обратный DN50')).toBeTruthy();
    expect(asked).not.toContain('scope=all');
    expect(screen.getAllByText('в работе').length).toBeGreaterThan(0);
    expect(screen.queryByText('Очередь')).toBeNull();
    expect(screen.getByRole('button', { name: 'Новая заявка' })).toBeTruthy();
  });

  it('a coordinator starts at the queue of new and taken requests', async () => {
    let asked = '';
    renderUi(page, {
      '/api/auth/me': coordinator,
      '/api/families/requests': (url: string) => {
        asked = url;
        return { items: [item({ urgency: 'Urgent' })], total: 1 };
      },
    }, '/families/requests');

    expect(await screen.findByText('Клапан обратный DN50')).toBeTruthy();
    expect(asked).toContain('scope=all');
    expect(decodeURIComponent(asked)).toContain('state=New,InWork');
    expect(screen.getAllByText('срочно').length).toBeGreaterThan(0);
  });

  it('a new request shows similar families and open requests while typing, then is filed', async () => {
    let filed: unknown = null;
    renderUi(page, {
      '/api/auth/me': designer,
      '/api/families/facets': { sections: [{ value: 'ОВ', count: 3 }], categories: [], years: [], placements: [] },
      '/api/families/requests/similar': {
        families: [{ id: 'c1', name: 'Клапан обратный межфланцевый', section: 'ОВ', relativePath: 'ОВ\\Клапан.rfa', category: 'Арматура', revitYear: 2022,
          typeCount: 4, state: 'Approved', hasPreview: false, updatedAt: '2026-11-20T10:00:00Z', previewHash: null, loadCount: 7 }],
        requests: [item({ id: 'r9', number: 9, title: 'Клапан обратный DN80', members: 3 })],
      },
      'POST /api/families/requests': (_: string, init?: RequestInit) => {
        filed = JSON.parse(String(init?.body));
        return { id: 'r10', number: 10 };
      },
      '/api/families/requests/r10': view({ id: 'r10', number: 10 }),
    }, '/families/requests/new?q=клапан обратный');

    expect(await screen.findByText('Клапан обратный межфланцевый', {}, { timeout: 3000 })).toBeTruthy();
    expect(screen.getByText(/№ 9 · Клапан обратный DN80/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText(/Подробности/), { target: { value: 'Ду50, межфланцевый' } });
    fireEvent.click(screen.getByRole('button', { name: 'Подать заявку' }));

    await waitFor(() => expect(filed).toEqual({ title: 'клапан обратный', text: 'Ду50, межфланцевый', section: '', urgency: 'Normal', project: '' }));
    expect(await screen.findByText(/№ 10/)).toBeTruthy();
  });

  it('opening a request with news marks it seen; another designer can join', async () => {
    const posted: string[] = [];
    renderUi(page, {
      '/api/auth/me': designer,
      '/api/families/requests/r1': view({ authorId: 'u7', authorName: 'Сидоров', isMember: false, unread: false }),
      'POST /api/families/requests/r1/join': () => {
        posted.push('join');
        return {};
      },
    }, '/families/requests/r1');

    expect(await screen.findByText(/№ 12 · Клапан обратный DN50/)).toBeTruthy();
    expect(screen.getByText('Каталог.pdf')).toBeTruthy();
    expect(screen.getByText(/взял в работу/)).toBeTruthy();
    expect(screen.getByText('Чтобы написать в заявку, присоединитесь к ней.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Мне тоже нужно' }));
    await waitFor(() => expect(posted).toEqual(['join']));
  });

  it('an unread request is marked seen when opened', async () => {
    const posted: string[] = [];
    renderUi(page, {
      '/api/auth/me': designer,
      '/api/families/requests/r1': view({ unread: true }),
      'POST /api/families/requests/r1/seen': () => {
        posted.push('seen');
        return {};
      },
    }, '/families/requests/r1');

    await waitFor(() => expect(posted).toEqual(['seen']));
  });

  it('a member writes a message', async () => {
    let sent: unknown = null;
    renderUi(page, {
      '/api/auth/me': designer,
      '/api/families/requests/r1': view(),
      'POST /api/families/requests/r1/messages': (_: string, init?: RequestInit) => {
        sent = JSON.parse(String(init?.body));
        return {};
      },
    }, '/families/requests/r1');

    fireEvent.change(await screen.findByLabelText('Сообщение'), { target: { value: 'нужен ещё DN100' } });
    fireEvent.click(screen.getByRole('button', { name: 'Отправить' }));

    await waitFor(() => expect(sent).toEqual({ text: 'нужен ещё DN100' }));
  });

  it('a coordinator takes the request and closes it with a family from the catalog', async () => {
    const posted: { url: string; body: unknown }[] = [];
    const record = (url: string, init?: RequestInit) => {
      posted.push({ url: url.split('?')[0], body: init?.body ? JSON.parse(String(init.body)) : null });
      return {};
    };
    renderUi(page, {
      '/api/auth/me': coordinator,
      '/api/families/requests/r1': view({ isMember: false }),
      '/api/families/requests/assignees': [{ id: 'u3', name: 'Петров' }],
      '/api/families/search': { items: [{ id: 'c1', name: 'Клапан обратный', section: 'ОВ', relativePath: '', category: '', revitYear: 2022, typeCount: 1,
        state: 'Approved', hasPreview: false, updatedAt: '2026-11-20T10:00:00Z', previewHash: null, loadCount: 0 }], total: 1 },
      'POST /api/families/requests/r1/take': record,
      'POST /api/families/requests/r1/done': record,
    }, '/families/requests/r1');

    fireEvent.click(await screen.findByRole('button', { name: 'Взять себе' }));
    await waitFor(() => expect(posted.map((p) => p.url)).toEqual(['/api/families/requests/r1/take']));
    fireEvent.click(screen.getByRole('button', { name: 'Готова…' }));
    fireEvent.change(await screen.findByLabelText('Найти семейство'), { target: { value: 'клапан' } });
    fireEvent.click(await within(screen.getByRole('dialog')).findByRole('button', { name: /Клапан обратный/ }));

    await waitFor(() => expect(posted.at(-1)).toEqual({ url: '/api/families/requests/r1/done', body: { cardId: 'c1' } }));
  });
});
