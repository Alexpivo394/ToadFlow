import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router';
import { AuthProvider } from '../auth/AuthContext';
import { renderUi } from '../test/renderApp';
import { CommandPalette } from './CommandPalette';

vi.setConfig({ testTimeout: 15_000 });

const admin = { id: 'u1', login: 'admin', displayName: 'Администратор', role: 'Admin', isDisabled: false, lastLoginAt: null, disciplines: [] };

const app = (
  <AuthProvider>
    <CommandPalette />
    <Routes>
      <Route path="/" element={<p>главная</p>} />
      <Route path="/families/requests/:id" element={<p>заявка открыта</p>} />
      <Route path="/families/catalog" element={<p>каталог открыт</p>} />
    </Routes>
  </AuthProvider>
);

describe('quick search', () => {
  it('Ctrl+K opens it, server hits are listed and Enter opens the first', async () => {
    renderUi(app, {
      '/api/auth/me': admin,
      '/api/modules': [{ name: 'families', title: 'Семейства', enabled: true }],
      '/api/search': [{ kind: 'request', title: '№ 12 · Клапан', subtitle: 'Заявка', link: '/families/requests/r12' }],
    });
    await screen.findByText('главная');

    fireEvent.keyDown(document, { key: 'k', ctrlKey: true });
    const input = await screen.findByPlaceholderText(/Найти/);
    fireEvent.change(input, { target: { value: 'клап' } });
    expect(await screen.findByText('№ 12 · Клапан')).toBeTruthy();
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(await screen.findByText('заявка открыта')).toBeTruthy();
  });

  it('finds pages without the server', async () => {
    renderUi(app, {
      '/api/auth/me': admin,
      '/api/modules': [{ name: 'families', title: 'Семейства', enabled: true }],
      '/api/search': [],
    });
    await screen.findByText('главная');

    fireEvent.keyDown(document, { key: 'k', ctrlKey: true });
    fireEvent.change(await screen.findByPlaceholderText(/Найти/), { target: { value: 'катал' } });
    fireEvent.click(await screen.findByText(/Каталог/));

    expect(await screen.findByText('каталог открыт')).toBeTruthy();
  });
});
