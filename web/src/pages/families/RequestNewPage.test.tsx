import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { RequestListItem } from './api';
import { RequestNewPage } from './RequestNewPage';

vi.setConfig({ testTimeout: 20_000 });

beforeEach(() => {
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:preview', revokeObjectURL: () => undefined }));
});

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: ['ОВ'] };
const directions = [
  { id: 'd1', name: 'ОВ/ВК', sections: ['ОВ'], disciplines: ['ОВ', 'ВК'], coordinators: [{ id: 'u3', name: 'Петров' }], hint: 'DN, присоединение, производитель' },
  { id: 'd2', name: 'АР/КР', sections: ['АР'], disciplines: ['АР'], coordinators: [], hint: '' },
];

const similarRequest = (patch: Partial<RequestListItem> = {}) => ({
  id: 'r5', number: 5, title: 'Клапан обратный DN80', section: 'ОВ', urgency: 'Normal', project: '', state: 'InWork', authorId: 'u7', authorName: 'Сидоров',
  assigneeId: null, assigneeName: null, cardId: null, cardName: null, members: 2, isMember: false, unread: false, createdAt: '2026-11-20T10:00:00Z',
  updatedAt: '2026-11-20T10:00:00Z', kind: 'New', directionId: 'd1', directionName: 'ОВ/ВК', neededBy: null, promisedBy: null, blocksIssue: false,
  overdue: false, turn: 'staff', waitingDays: 2, thumbnailFileId: null, revitYear: null, targetCardId: null, targetCardName: null, resolution: null,
  reviewSince: null, ...patch,
});

const page = (
  <AuthProvider>
    <Routes>
      <Route path="/families/requests/new" element={<RequestNewPage />} />
      <Route path="/families/requests/:id" element={<p>страница заявки</p>} />
    </Routes>
  </AuthProvider>
);

const base = {
  '/api/auth/me': designer,
  '/api/families/directions': directions,
  '/api/families/facets': { sections: [{ value: 'ОВ', count: 3 }, { value: 'АР', count: 1 }], categories: [], years: [], placements: [] },
  '/api/families/requests/projects': ['ЖК Север'],
  '/api/families/requests/similar': { families: [], requests: [] },
};

describe('new request', () => {
  it('takes context from Revit, shows where it goes and submits with attachments', async () => {
    let body: Record<string, unknown> | null = null;
    renderUi(page, {
      ...base,
      'POST /api/families/requests/uploads': { id: 'f1', fileName: 'скрин.png', size: 10, contentType: 'image/png' },
      'POST /api/families/requests': (_: string, init?: RequestInit) => { body = JSON.parse(String(init?.body)); return { id: 'r9', number: 9 }; },
    }, '/families/requests/new?q=Клапан%20обратный&project=ЖК%20Север&section=ОВ&revit=2024');

    expect(((await screen.findByLabelText(/Что нужно/)) as HTMLInputElement).value).toBe('Клапан обратный');
    expect(await screen.findByText(/ОВ\/ВК — Петров/)).toBeTruthy();
    expect((screen.getByPlaceholderText(/DN, присоединение/) as HTMLTextAreaElement)).toBeTruthy();

    fireEvent.change(screen.getByTestId('drop-input'), { target: { files: [new File([new Uint8Array(10)], 'скрин.png', { type: 'image/png' })] } });
    expect(await screen.findByText('скрин.png')).toBeTruthy();
    await waitFor(() => expect((screen.getByRole('button', { name: 'Подать заявку' }) as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(screen.getByRole('button', { name: 'Подать заявку' }));

    expect(await screen.findByText('страница заявки')).toBeTruthy();
    expect(body).toMatchObject({ title: 'Клапан обратный', project: 'ЖК Север', section: 'ОВ', revitYear: 2024, directionId: 'd1', fileIds: ['f1'], kind: 'New' });
  });

  it('a problem in a catalog family is a fix request for that card', async () => {
    let body: Record<string, unknown> | null = null;
    renderUi(page, {
      ...base,
      '/api/families/c1': { id: 'c1', name: 'Дверь ДГ', section: 'АР' },
      'POST /api/families/requests': (_: string, init?: RequestInit) => { body = JSON.parse(String(init?.body)); return { id: 'r9', number: 9 }; },
    }, '/families/requests/new?kind=fix&card=c1');

    expect(await screen.findByText(/Дверь ДГ/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText(/Что не так/), { target: { value: 'Не тот уровень' } });
    fireEvent.click(screen.getByRole('button', { name: 'Подать заявку' }));

    await screen.findByText('страница заявки');
    expect(body).toMatchObject({ kind: 'Fix', targetCardId: 'c1', title: 'Не тот уровень' });
  });

  it('joins a similar open request instead of filing a copy', async () => {
    let joined = false;
    renderUi(page, {
      ...base,
      '/api/families/requests/similar': { families: [], requests: [similarRequest()] },
      'POST /api/families/requests/r5/join': () => { joined = true; return {}; },
    }, '/families/requests/new?q=клапан%20обратный');

    fireEvent.click(await screen.findByRole('button', { name: 'Мне тоже нужно' }));

    expect(await screen.findByText('страница заявки')).toBeTruthy();
    expect(joined).toBe(true);
  });
});
