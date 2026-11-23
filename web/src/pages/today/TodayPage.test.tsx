import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import { TodayPage } from './TodayPage';

vi.setConfig({ testTimeout: 15_000 });

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: ['ОВ'] };

describe('for you', () => {
  it('a designer sees what waits for them and quick actions', async () => {
    renderUi(
      <AuthProvider>
        <Routes>
          <Route path="/" element={<TodayPage />} />
          <Route path="/families/requests" element={<p>мои заявки</p>} />
        </Routes>
      </AuthProvider>,
      {
        '/api/auth/me': designer,
        '/api/modules': [{ name: 'families', title: 'Семейства', enabled: true }, { name: 'clash', title: 'Коллизии', enabled: true }],
        '/api/me/today': { items: [
          { module: 'families', text: 'Заявки ждут вашего ответа или приёмки', link: '/families/requests?scope=mine', tone: 'Action', count: 2 },
          { module: null, text: 'Выгрузка: расписания с ошибками за сутки', link: '/export/history', tone: 'Action', count: 1, resolveKey: 'export' },
        ] },
      });

    expect(await screen.findByRole('heading', { name: 'Для вас' })).toBeTruthy();
    expect(screen.getByText('2')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Решено' })).toBeNull(); // отмечает Координатор и выше
    expect(screen.getByRole('link', { name: /Подать заявку/ }).getAttribute('href')).toBe('/families/requests/new');
    fireEvent.click(screen.getByText('Заявки ждут вашего ответа или приёмки'));
    expect(await screen.findByText('мои заявки')).toBeTruthy();
  });

  it('nothing waiting — says so', async () => {
    renderUi(<AuthProvider><TodayPage /></AuthProvider>, { '/api/auth/me': designer, '/api/me/today': { items: [] } });
    expect(await screen.findByText(/Сейчас ничего не ждёт/)).toBeTruthy();
  });
});
