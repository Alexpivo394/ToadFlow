import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { renderUi } from '../../test/renderApp';
import { SettingsPage } from './SettingsPage';

const user = (role: string) => ({ id: 'u1', login: role.toLowerCase(), displayName: role, role, isDisabled: false, lastLoginAt: null, disciplines: [] });

function render(role: string) {
  const saved: unknown[] = [];
  renderUi(<AuthProvider><SettingsPage /></AuthProvider>, {
    '/api/auth/me': user(role),
    '/api/usage/settings': { retentionMonths: 13 },
    'PUT /api/usage/settings': (_url: string, init?: RequestInit) => {
      saved.push(JSON.parse(String(init?.body)));
      return { retentionMonths: 24 };
    },
  });
  return saved;
}

const input = () => screen.getAllByLabelText('Хранить данные, месяцев').find((e) => e.tagName === 'INPUT') as HTMLInputElement;

describe('usage SettingsPage', () => {
  it('viewer sees the retention but cannot change it', async () => {
    render('Viewer');

    await screen.findByDisplayValue('13');
    expect(input().disabled).toBe(true);
    expect(screen.queryByRole('button', { name: 'Сохранить' })).toBeNull();
  });

  it('administrator saves a new retention', async () => {
    const saved = render('Admin');
    await screen.findByRole('button', { name: 'Сохранить' });

    const field = screen.getByDisplayValue('13');
    fireEvent.change(field, { target: { value: '24' } });
    fireEvent.blur(field);
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(saved).toEqual([{ retentionMonths: 24 }]));
  });

  it('out of range is shown and cannot be saved', async () => {
    render('Admin');
    await screen.findByRole('button', { name: 'Сохранить' });

    const field = screen.getByDisplayValue('13');
    fireEvent.change(field, { target: { value: '200' } });
    fireEvent.blur(field);

    expect(await screen.findByText('От 1 до 120 месяцев')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Сохранить' })).toHaveProperty('disabled', true);
  });
});
