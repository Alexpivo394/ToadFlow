import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { renderUi } from '../test/renderApp';
import type { NotificationItem } from './api';
import { ago, NotificationBell } from './NotificationBell';

vi.setConfig({ testTimeout: 15_000 });

const item = (patch: Partial<NotificationItem> = {}): NotificationItem => ({
  id: 1, kind: 'request.review', title: 'Заявка № 12', text: '«Клапан»: готово — проверьте', link: '/families/requests/r1', count: 1,
  at: new Date().toISOString(), read: false, ...patch,
});

describe('notification bell', () => {
  it('shows the unread count, opens the list and follows a link marking it read', async () => {
    document.title = 'ToadFlow';
    const read: string[] = [];
    renderUi(
      <Routes>
        <Route path="/" element={<NotificationBell />} />
        <Route path="/families/requests/:id" element={<p>страница заявки</p>} />
      </Routes>,
      {
        '/api/notifications/count': { unread: 3 },
        '/api/notifications': [item(), item({ id: 2, title: 'Обновлено: Дверь', read: true, at: '2026-01-02T10:00:00Z', link: null })],
        'POST /api/notifications/1/read': (url: string) => { read.push(url); return {}; },
      });

    expect(await screen.findByText('3')).toBeTruthy();
    await waitFor(() => expect(document.title).toBe('(3) ToadFlow'));
    fireEvent.click(screen.getByRole('button', { name: 'Уведомления' }));
    expect(await screen.findByText('Заявка № 12')).toBeTruthy();
    expect(screen.getByText('Сегодня')).toBeTruthy();
    expect(screen.getByText('Ранее')).toBeTruthy();

    fireEvent.click(screen.getByText('Заявка № 12'));
    expect(await screen.findByText('страница заявки')).toBeTruthy();
    expect(read).toHaveLength(1);
  });

  it('reads all', async () => {
    let all = false;
    renderUi(<NotificationBell />, {
      '/api/notifications/count': { unread: 1 },
      '/api/notifications': [item()],
      'POST /api/notifications/read-all': () => { all = true; return {}; },
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Уведомления' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Прочитать все' }));
    await waitFor(() => expect(all).toBe(true));
  });

  it('formats time', () => {
    const now = new Date('2026-11-22T12:00:00');
    expect(ago('2026-11-22T11:55:00', now)).toBe('5 мин назад');
    expect(ago('2026-11-21T09:30:00', now)).toBe('вчера в 09:30');
  });
});

describe('notification settings', () => {
  it('unticking a kind saves it as muted', async () => {
    let saved: unknown = null;
    const { NotificationSettingsPanel } = await import('./NotificationSettingsPanel');
    renderUi(<NotificationSettingsPanel />, {
      '/api/notifications/settings': {
        kinds: [{ kind: 'run.failed', group: 'Ферма', title: 'Ошибки в расписании', muted: false },
          { kind: 'clash.new', group: 'Коллизии', title: 'Новые коллизии', muted: true }],
      },
      'PUT /api/notifications/settings': (_: string, init?: RequestInit) => { saved = JSON.parse(String(init?.body)); return { kinds: [] }; },
    });

    fireEvent.click(await screen.findByLabelText('Ошибки в расписании'));
    await waitFor(() => expect(saved).toEqual({ muted: ['run.failed', 'clash.new'] }));
  });
});
