import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { copyText } from '../../lib/clipboard';
import { renderUi } from '../../test/renderApp';
import type { FamilyCardView } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';

vi.mock('../../lib/clipboard', () => ({ copyText: vi.fn(async () => true) }));

const viewer = { id: 'u3', login: 'viewer', displayName: 'Наблюдатель', role: 'Viewer', isDisabled: false, lastLoginAt: null, disciplines: [] };
const coordinator = { ...viewer, id: 'u4', role: 'Operator' };

const card = (patch: Partial<FamilyCardView> = {}): FamilyCardView => ({
  id: 'c1', name: 'Клапан обратный', relativePath: 'ОВ\Клапаны\Клапан обратный.rfa', fullPath: '\\nas\Families\ОВ\Клапаны\Клапан обратный.rfa',
  section: 'ОВ', state: 'Approved', category: 'Арматура трубопроводов', placement: 'OneLevelBased', revitYear: 2022, typeCount: 2,
  description: null, tags: [], manufacturer: null, createdAt: '2026-11-19T10:00:00Z', updatedAt: '2026-11-20T10:00:00Z', archivedAt: null,
  currentVersionId: 'v2', currentReadState: 'Done', currentReadError: null, hasPreview: false,
  content: {
    category: 'Арматура трубопроводов', placement: 'OneLevelBased', revitYear: 2022, typesFromCatalog: true,
    types: [{ name: 'КЛАБ-40-20', values: { 'Диаметр условный': '40 мм' } }, { name: 'КЛАБ-50-20', values: { 'Диаметр условный': '50 мм' } }],
    parameters: [{ name: 'Диаметр условный', isShared: true, sharedGuid: 'g1', isInstance: false, group: 'Размеры', dataType: 'Длина' }],
  },
  versions: [
    { id: 'v2', hash: 'b', revitYear: 2022, state: 'Approved', note: 'изменено без проверки', relativePath: 'ОВ\Клапаны\Клапан обратный.rfa', seenAt: '2026-11-20T10:00:00Z', readState: 'Done', readError: null, hasPreview: false, isCurrent: true, authorName: null, comment: null },
    { id: 'v1', hash: 'a', revitYear: 2022, state: 'Approved', note: '', relativePath: 'ОВ\Клапаны\Клапан обратный.rfa', seenAt: '2026-11-19T10:00:00Z', readState: 'Failed', readError: 'Revit не открыл файл', hasPreview: false, isCurrent: false, authorName: 'Петров', comment: 'первая версия' },
  ],
  loadCount: 3, lastLoadedAt: '2026-11-20T12:00:00Z',
  ...patch,
});

function render(view: FamilyCardView, user?: object, extra: Record<string, unknown> = {}) {
  renderUi(<AuthProvider><FamilyCardDrawer cardId="c1" unreviewed={false} onClose={() => {}} /></AuthProvider>, {
    ...(user ? { '/api/auth/me': user } : {}),
    '/api/families/c1': view,
    ...extra,
  });
}

describe('FamilyCardDrawer', () => {
  it('card shows the full path and copies it', async () => {
    render(card());

    expect(await screen.findByText('\\nas\Families\ОВ\Клапаны\Клапан обратный.rfa')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Скопировать путь' }));

    await waitFor(() => expect(vi.mocked(copyText)).toHaveBeenCalledWith('\\nas\Families\ОВ\Клапаны\Клапан обратный.rfa', expect.anything()));
    expect(await screen.findByRole('button', { name: 'Путь скопирован' })).toBeTruthy();
  });

  it('card of an unread version says the farm is reading it', async () => {
    render(card({ content: null, currentReadState: 'Pending', typeCount: 0 }));

    expect(await screen.findByText(/Содержимое читается фермой/)).toBeTruthy();
    expect(screen.queryByRole('tab', { name: 'Типоразмеры' })).toBeNull();
    expect(screen.getByRole('tab', { name: 'Версии' })).toBeTruthy();
  });

  it('card of an unreadable version shows the reason', async () => {
    render(card({ content: null, currentReadState: 'Failed', currentReadError: 'Revit не открыл файл: The file is corrupt.' }));

    expect(await screen.findByText('Revit не открыл файл: The file is corrupt.')).toBeTruthy();
  });

  it('card lists types, parameters and versions', async () => {
    render(card());

    expect(await screen.findByText('КЛАБ-40-20')).toBeTruthy();
    expect(screen.getByText('50 мм')).toBeTruthy();
    expect(screen.getByText('Типоразмеры из каталога .txt')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: 'Параметры' }));
    const parameters = await screen.findByRole('tabpanel', { name: 'Параметры' });
    expect(within(parameters).getByText('Диаметр условный')).toBeTruthy();
    expect(within(parameters).getByText('тип')).toBeTruthy();
    expect(within(parameters).getByText('Размеры')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: 'Версии' }));
    const versions = await screen.findByRole('tabpanel', { name: 'Версии' });
    expect(within(versions).getByText('изменено без проверки')).toBeTruthy();
    expect(within(versions).getByText('действующая')).toBeTruthy();
    expect(within(versions).getByText('Revit не открыл файл')).toBeTruthy();
  });

  it('operator can edit description and tags; viewer cannot', async () => {
    const saved: unknown[] = [];
    render(card(), coordinator, {
      'PUT /api/families/c1/notes': (_url: string, init?: RequestInit) => {
        saved.push(JSON.parse(String(init?.body)));
        return {};
      },
    });

    fireEvent.change(await screen.findByLabelText('Описание'), { target: { value: 'Межфланцевый' } });
    fireEvent.change(screen.getByLabelText('Производитель'), { target: { value: 'ВЕЗА' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(saved).toEqual([{ description: 'Межфланцевый', tags: [], manufacturer: 'ВЕЗА' }]));
  });

  it('viewer sees no editor', async () => {
    render(card(), viewer);

    await screen.findByText('КЛАБ-40-20');
    expect(screen.queryByLabelText('Описание')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('coordinator archives the card and reverts to a previous version; loads and authors are shown', async () => {
    const posted: string[] = [];
    render(card(), undefined, {
      '/api/families/actions': { available: true, reason: null },
      'POST /api/families/c1/archive': () => {
        posted.push('archive');
        return {};
      },
      'POST /api/families/versions/v1/revert': () => {
        posted.push('revert');
        return {};
      },
    });

    expect(await screen.findByText(/Загружали в проекты: 3 раза/)).toBeTruthy();
    fireEvent.click(screen.getByRole('tab', { name: 'Версии' }));
    expect(await screen.findByText('первая версия')).toBeTruthy();
    expect(screen.getByText('Петров')).toBeTruthy();
    expect(screen.getByText('автор неизвестен')).toBeTruthy();

    const archive = screen.getByRole('button', { name: 'В архив' });
    await waitFor(() => expect((archive as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(archive);
    fireEvent.click(await screen.findByRole('button', { name: 'Да' }));
    await waitFor(() => expect(posted).toEqual(['archive']));

    fireEvent.click(screen.getByRole('button', { name: 'Вернуть эту' }));
    fireEvent.click((await screen.findAllByRole('button', { name: 'Да' })).at(-1)!);
    await waitFor(() => expect(posted).toEqual(['archive', 'revert']));
  });

  it('designer sees no coordinator buttons', async () => {
    render(card(), { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: [] });

    expect(await screen.findByText(/Загружали в проекты/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'В архив' })).toBeNull();
  });
});
