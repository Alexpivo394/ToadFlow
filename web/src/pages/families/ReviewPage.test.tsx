import { screen } from '@testing-library/react';
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
    expect(screen.getByText(/переложите файл из папки проверки на место в библиотеке/)).toBeTruthy();
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
    expect(screen.queryByRole('button', { name: /Удалить|Переложить|Утвердить/ })).toBeNull();
  });

  it('nothing to decide says so', async () => {
    renderUi(<AuthProvider><IssuesPage /></AuthProvider>, { '/api/families/issues': [] });

    expect(await screen.findByText('Решать нечего')).toBeTruthy();
  });
});
