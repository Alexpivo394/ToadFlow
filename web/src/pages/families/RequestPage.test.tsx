import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { RequestListItem, RequestView } from './api';
import { RequestPage } from './RequestPage';

vi.setConfig({ testTimeout: 20_000 });

beforeEach(() => {
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:preview', revokeObjectURL: () => undefined }));
});

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: [] };
const coordinator = { id: 'u3', login: 'petrov', displayName: 'Петров', role: 'Operator', isDisabled: false, lastLoginAt: null, disciplines: [] };

const item = (patch: Partial<RequestListItem> = {}): RequestListItem => ({
  id: 'r1', number: 12, title: 'Клапан обратный DN50', section: 'ОВ', urgency: 'Normal', project: 'ЖК Север', state: 'InWork', authorId: 'u2',
  authorName: 'Иванов', assigneeId: 'u3', assigneeName: 'Петров', cardId: null, cardName: null, members: 1, isMember: true, unread: false,
  createdAt: '2026-11-20T10:00:00Z', updatedAt: '2026-11-20T10:00:00Z', kind: 'New', directionId: 'd1', directionName: 'ОВ/ВК', neededBy: '2026-12-01',
  promisedBy: null, blocksIssue: true, overdue: false, turn: 'staff', waitingDays: 2, thumbnailFileId: null, revitYear: 2024, targetCardId: null,
  targetCardName: null, resolution: null, reviewSince: null, ...patch,
});

const view = (patch: Partial<RequestListItem> = {}, extra: Partial<RequestView> = {}): RequestView => ({
  request: item(patch), text: 'межфланцевый, https://example.ru/catalog', reason: null, members: [{ userId: 'u2', name: 'Иванов' }],
  messages: [
    { id: 'm1', authorId: 'u3', authorName: 'Петров', text: 'взял в работу', isEvent: true, at: '2026-11-20T11:00:00Z', fileIds: [] },
    { id: 'm2', authorId: 'u2', authorName: 'Иванов', text: 'вот куда встанет', isEvent: false, at: '2026-11-20T12:00:00Z', fileIds: ['f2'] },
  ],
  files: [
    { id: 'f1', fileName: 'Каталог.pdf', size: 2048, contentType: 'application/pdf', uploadedByName: 'Иванов', at: '2026-11-20T10:05:00Z', messageId: null, uploadedBy: 'u2', inline: true },
    { id: 'f2', fileName: 'план.png', size: 4096, contentType: 'image/png', uploadedByName: 'Иванов', at: '2026-11-20T12:00:00Z', messageId: 'm2', uploadedBy: 'u2', inline: true },
  ],
  autoAcceptAt: null,
  ...extra,
});

const page = (
  <AuthProvider>
    <Routes>
      <Route path="/families/requests/:id" element={<RequestPage />} />
    </Routes>
  </AuthProvider>
);

const common = {
  '/api/families/requests/assignees': [{ id: 'u3', name: 'Петров' }],
  '/api/families/directions': [{ id: 'd1', name: 'ОВ/ВК', sections: [], disciplines: [], coordinators: [], hint: '' }],
  'POST /api/notifications/read-link': {},
  '/api/families/requests/r1/files/f2': new ArrayBuffer(4),
  '/api/families/requests/r1/files/f1': new ArrayBuffer(4),
};

describe('request page', () => {
  it('shows whose move it is, deadlines, gallery and a message with its attachment', async () => {
    renderUi(page, { ...common, '/api/auth/me': designer, '/api/families/requests/r1': view() }, '/families/requests/r1');

    expect(await screen.findByText(/Клапан обратный DN50/)).toBeTruthy();
    expect(screen.getByText('Ход координатора')).toBeTruthy();
    expect(screen.getByText('Блокирует выдачу')).toBeTruthy();
    expect(screen.getByText('01.12.2026')).toBeTruthy();
    expect(screen.getByRole('link', { name: 'https://example.ru/catalog' })).toBeTruthy();
    const message = screen.getByText('вот куда встанет').closest('[data-message]') as HTMLElement;
    expect(within(message).getByText('план.png')).toBeTruthy();
    expect(screen.getAllByText('Каталог.pdf').length).toBeGreaterThan(0);
  });

  it('the author accepts or declines with a reason on review', async () => {
    let declined: unknown = null;
    renderUi(page, {
      ...common,
      '/api/auth/me': designer,
      '/api/families/requests/r1': view({ state: 'Review', turn: 'author', cardId: 'c1', cardName: 'Клапан обратный', resolution: 'Made' },
        { autoAcceptAt: '2026-11-27T10:00:00Z' }),
      'POST /api/families/requests/r1/decline': (_: string, init?: RequestInit) => { declined = JSON.parse(String(init?.body)); return {}; },
    }, '/families/requests/r1');

    expect(await screen.findByText('Координатор сделал. Подходит?')).toBeTruthy();
    expect(screen.getByText('Ваш ход')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Подходит' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Не то…' }));
    const dialog = await screen.findByRole('dialog');
    const send = within(dialog).getByRole('button', { name: 'Отправить' }) as HTMLButtonElement;
    expect(send.disabled).toBe(true);
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Не тот DN' } });
    fireEvent.click(send);

    await waitFor(() => expect(declined).toMatchObject({ text: 'Не тот DN' }));
  });

  it('a coordinator asks the author with the composer text', async () => {
    let asked: unknown = null;
    renderUi(page, {
      ...common,
      '/api/auth/me': coordinator,
      '/api/families/requests/r1': view({ isMember: false }),
      'POST /api/families/requests/r1/ask': (_: string, init?: RequestInit) => { asked = JSON.parse(String(init?.body)); return {}; },
    }, '/families/requests/r1');

    fireEvent.click(await screen.findByRole('button', { name: 'Какой производитель и марка?' }));
    fireEvent.click(screen.getByRole('button', { name: 'Спросить автора' }));

    await waitFor(() => expect(asked).toMatchObject({ text: 'Какой производитель и марка?', fileIds: [] }));
  });

  it('the author is asked a question and can withdraw', async () => {
    let withdrawn = false;
    renderUi(page, {
      ...common,
      '/api/auth/me': designer,
      '/api/families/requests/r1': view({ state: 'NeedInfo', turn: 'author' }),
      'POST /api/families/requests/r1/withdraw': () => { withdrawn = true; return {}; },
    }, '/families/requests/r1');

    expect(await screen.findByText('Координатор ждёт вашего ответа')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Отозвать' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Отозвать' }));

    await waitFor(() => expect(withdrawn).toBe(true));
  });
});
