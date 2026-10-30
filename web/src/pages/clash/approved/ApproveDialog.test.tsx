import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { partResponses } from '../../../test/clashPart';
import { Failure, renderApp } from '../../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const approvals = () => (fetch as unknown as FetchMock).mock.calls.filter(([url, init]) => url === '/api/clash/parts/p1/approve' && init?.method === 'POST');

async function openDialog(responses: Record<string, unknown>) {
  renderApp('/clash/setup/parts/p1?tab=draft', { ...partResponses, ...responses });
  fireEvent.click(await screen.findByRole('button', { name: 'Утвердить…' }));
  return screen.findByRole('dialog');
}

describe('Approve dialog', () => {
  it('ApproveDialog_RunCheckedByDefault', async () => {
    await openDialog({ 'POST /api/clash/parts/p1/approve': { version: 1, approvedAt: '2026-10-29T10:00:00Z', userName: 'Администратор', comment: 'первая', runId: 'r9' } });
    const run = screen.getByLabelText('Сразу прогнать утверждённую версию') as HTMLInputElement;
    expect(run.checked).toBe(true);
    fireEvent.change(screen.getByLabelText('Комментарий'), { target: { value: 'первая' } });

    fireEvent.click(screen.getByRole('button', { name: 'Утвердить' }));

    await waitFor(() => expect(approvals()).toHaveLength(1));
    expect(JSON.parse(String(approvals()[0][1]!.body))).toEqual({ comment: 'первая', run: true });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('ApproveDialog_ShowsProblemsOn400', async () => {
    await openDialog({
      'POST /api/clash/parts/p1/approve': new Failure(400, 'В матрице нет включённых проверок', { errors: ['В матрице нет включённых проверок'] }),
    });

    fireEvent.click(screen.getByRole('button', { name: 'Утвердить' }));

    expect(await screen.findByText('В матрице нет включённых проверок')).toBeTruthy();
    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  /** Утверждает координатор; наблюдатель в настройку части не попадает. */
  it('ApproveDialog_ObserverHasNoSetup', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', {
      ...partResponses,
      '/api/auth/me': { id: 'u2', login: 'viewer', displayName: 'Наблюдатель', role: 'Viewer', isDisabled: false, lastLoginAt: null },
    });

    expect(await screen.findByRole('heading', { name: 'Проверки' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Прогнать' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Утвердить…' })).toBeNull();
  });
});
