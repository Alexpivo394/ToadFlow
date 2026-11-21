import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import type { FamilyIssue, FamilyReviewItem } from './api';
import { IssuesPage } from './IssuesPage';
import { ReviewPage } from './ReviewPage';

const items: FamilyReviewItem[] = [
  { cardId: 'c1', versionId: 'v1', name: 'Кран шаровой', relativePath: '_На проверке\\ВК\\Кран шаровой.rfa', isNew: true, revitYear: 2022, seenAt: '2026-11-20T10:00:00Z' },
  { cardId: 'c2', versionId: 'v2', name: 'Клапан обратный', relativePath: '_На проверке\\Клапан обратный.rfa', isNew: false, revitYear: 2024, seenAt: '2026-11-20T11:00:00Z' },
];

const issues: FamilyIssue[] = [
  {
    id: 'i1', kind: 'NameConflict', cardId: 'c1', cardName: 'Клапан', relativePath: 'ВК\\Клапан.rfa', createdAt: '2026-11-20T10:00:00Z', resolvedAt: null,
    text: 'В библиотеке уже есть семейство «Клапан» в «ОВ\\Клапан.rfa». Переименуйте один из файлов или удалите лишний.',
  },
  {
    id: 'i2', kind: 'Unreadable', cardId: 'c2', cardName: 'Балка', relativePath: 'КР\\Балка.rfa', createdAt: '2026-11-20T11:00:00Z', resolvedAt: null,
    text: 'Файл «КР\\Балка.rfa» не удалось прочитать в Revit: Revit не открыл файл.',
  },
  {
    id: 'i3', kind: 'UnreviewedChange', cardId: 'c3', cardName: 'Отвод', relativePath: 'ОВ\\Отвод.rfa', createdAt: '2026-11-20T12:00:00Z', resolvedAt: null,
    text: 'Файл «ОВ\\Отвод.rfa» перезаписан на месте, мимо папки проверки.',
  },
];

describe('ReviewPage', () => {
  it('review page lists new families and candidate versions and explains how to approve', async () => {
    renderUi(<AuthProvider><ReviewPage /></AuthProvider>, { '/api/families/review': items });

    expect(await screen.findByText('Кран шаровой')).toBeTruthy();
    expect(screen.getByText('Клапан обратный')).toBeTruthy();
    expect(screen.getByText('_На проверке\\ВК\\Кран шаровой.rfa')).toBeTruthy();
    expect(screen.getAllByText('новое семейство').length).toBeGreaterThan(0);
    expect(screen.getAllByText('новая версия').length).toBeGreaterThan(0);
    expect(screen.getByText(/Утвердите кнопкой — файл переложит агент библиотеки/)).toBeTruthy();
  });

  it('empty review folder says so', async () => {
    renderUi(<AuthProvider><ReviewPage /></AuthProvider>, { '/api/families/review': [] });

    expect(await screen.findByText('В папке проверки ничего нет')).toBeTruthy();
  });
});

describe('IssuesPage', () => {
  it('issues page explains each kind of case in plain words', async () => {
    renderUi(<AuthProvider><IssuesPage /></AuthProvider>, { '/api/families/issues': issues });

    expect(await screen.findByText(/Переименуйте один из файлов или удалите лишний/)).toBeTruthy();
    expect(screen.getAllByText('Конфликт имён').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Не читается').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Изменено без проверки').length).toBeGreaterThan(0);
    expect(screen.getByText('Балка')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Переименовать…' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Прочитать снова' })).toHaveLength(1);
  });

  it('an unreviewed change is marked seen; other cases have no button', async () => {
    const resolved: string[] = [];
    renderUi(<AuthProvider><IssuesPage /></AuthProvider>, {
      '/api/families/issues': issues,
      'POST /api/families/issues/i3/resolve': () => {
        resolved.push('i3');
        return {};
      },
    });

    const buttons = await screen.findAllByRole('button', { name: 'Посмотрел' });
    expect(buttons).toHaveLength(1);
    fireEvent.click(buttons[0]);

    await waitFor(() => expect(resolved).toEqual(['i3']));
  });

  it('nothing to decide says so', async () => {
    renderUi(<AuthProvider><IssuesPage /></AuthProvider>, { '/api/families/issues': [] });

    expect(await screen.findByText('Решать нечего')).toBeTruthy();
  });
});

describe('file actions', { timeout: 20_000 }, () => {
  const available = { '/api/families/actions': { available: true, reason: null } };

  it('a candidate is approved after confirmation, a new family into a chosen folder', async () => {
    const calls: { url: string; body: unknown }[] = [];
    const record = (url: string, init?: RequestInit) => {
      calls.push({ url: url.split('?')[0], body: init?.body ? JSON.parse(String(init.body)) : null });
      return {};
    };
    renderUi(<AuthProvider><ReviewPage /></AuthProvider>, {
      ...available,
      '/api/families/review': items,
      '/api/families/folders': ['ВК', 'ОВ\Клапаны'],
      'POST /api/families/versions/v2/approve': record,
      'POST /api/families/c1/approve': record,
    });

    const approve = await screen.findByRole('button', { name: 'Утвердить' });
    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(approve);
    fireEvent.click(await screen.findByRole('button', { name: 'Да' }));
    await waitFor(() => expect(calls.map((c) => c.url)).toEqual(['/api/families/versions/v2/approve']));

    fireEvent.click(screen.getByRole('button', { name: 'Утвердить и переложить в…' }));
    fireEvent.change(await screen.findByPlaceholderText('ОВ\\Клапаны'), { target: { value: 'ВК\\Краны' } });
    fireEvent.click(within(screen.getAllByRole('dialog').at(-1)!).getByRole('button', { name: 'Утвердить' }));
    await waitFor(() => expect(calls.at(-1)).toEqual({ url: '/api/families/c1/approve', body: { folder: 'ВК\\Краны' } }));
  });

  it('a version is rejected with a reason', async () => {
    let body: unknown = null;
    renderUi(<AuthProvider><ReviewPage /></AuthProvider>, {
      ...available,
      '/api/families/review': items,
      'POST /api/families/versions/v2/reject': (_: string, init?: RequestInit) => {
        body = JSON.parse(String(init?.body));
        return {};
      },
    });

    const row = (await screen.findByText('_На проверке\\Клапан обратный.rfa')).closest('tr')!;
    const reject = within(row as HTMLElement).getByRole('button', { name: 'Отклонить…' });
    await waitFor(() => expect((reject as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(reject);
    fireEvent.change(await screen.findByLabelText('Причина'), { target: { value: 'не тот DN' } });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Отклонить…' }));

    await waitFor(() => expect(body).toEqual({ reason: 'не тот DN' }));
  });

  it('buttons are disabled while the library agent cannot act', async () => {
    renderUi(<AuthProvider><ReviewPage /></AuthProvider>, {
      '/api/families/actions': { available: false, reason: 'агент библиотеки BIM-LIB-01 старой версии' },
      '/api/families/review': items,
    });

    const approve = await screen.findByRole('button', { name: 'Утвердить' });
    await waitFor(() => expect((approve as HTMLButtonElement).disabled).toBe(true));
  });

  it('open cases are answered with buttons', async () => {
    const answers: unknown[] = [];
    const answer = (_: string, init?: RequestInit) => {
      answers.push(JSON.parse(String(init?.body)));
      return {};
    };
    const more: FamilyIssue[] = [
      { id: 'i4', kind: 'Duplicate', cardId: 'c1', cardName: 'Клапан', relativePath: 'ОВ\Клапан копия.rfa', createdAt: '2026-11-20T10:00:00Z', resolvedAt: null, text: 'копия' },
      { id: 'i5', kind: 'Ambiguous', cardId: 'c2', cardName: 'Кран', relativePath: 'ОВ\Кран новый.rfa', createdAt: '2026-11-20T10:00:00Z', resolvedAt: null, text: 'неясно' },
    ];
    renderUi(<AuthProvider><IssuesPage /></AuthProvider>, {
      ...available,
      '/api/families/issues': [...issues, ...more],
      'POST /api/families/issues/i4/answer': answer,
      'POST /api/families/issues/i5/answer': answer,
      'POST /api/families/issues/i2/answer': answer,
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Оставить' }));
    fireEvent.click(screen.getByRole('button', { name: 'Новая версия того' }));
    fireEvent.click(screen.getByRole('button', { name: 'Прочитать снова' }));

    await waitFor(() => expect(answers).toEqual([{ answer: 'keep', name: null }, { answer: 'same', name: null }, { answer: 'retry', name: null }]));
  });
});
