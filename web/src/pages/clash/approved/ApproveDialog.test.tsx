import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { partResponses } from '../../../test/clashPart';
import { Failure, renderApp, renderUi } from '../../../test/renderApp';
import { ApproveDialog } from './ApproveDialog';
import { ApprovePreviewBlock } from './ApprovePreviewBlock';

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
    expect(JSON.parse(String(approvals()[0][1]!.body))).toEqual({ comment: 'первая', run: true, schedules: [] });
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

const schedule = (id: string, name: string, selected: boolean) => ({
  id, name, selected, isEnabled: true, recurrenceKind: 'Weekly', daysOfWeekMask: 0b1111111, timeOfDay: '02:00', intervalHours: null, onceAt: null, nextRunAt: null,
});

const preview = (extra: object = {}) => ({
  draftRunAt: '2026-11-02T10:30:00Z', stale: false, neverRun: false, firstApproval: false, changes: [{ kind: 'CellTolerance', text: 'АР × ОВ: допуск 20 → 35 мм' }],
  pairs: Array.from({ length: 10 }, (_, i) => ({ pair: `П${i + 1}`, gone: (i + 1) * 100, new: i, same: 1000 })),
  ...extra,
});

describe('Approve dialog preview and schedules', () => {
  it('ApproveDialog_PreviewTable', async () => {
    const dialog = await openDialog({ '/api/clash/parts/p1/approve/preview': preview() });

    expect(await screen.findByText('Если утвердить')).toBeTruthy();
    const rows = dialog.querySelectorAll('[data-testid="preview-row"]');
    expect([...rows].map((r) => r.firstElementChild!.textContent)).toEqual(['П10', 'П9', 'П8', 'П7', 'П6', 'П5', 'П4', 'П3']);
    expect(rows[0].textContent).toContain('≈ 1 000');
    expect(dialog.textContent).toContain('ещё 2 пары');
    expect(screen.getByTestId('preview-total').textContent).toMatch(/Итого.*≈\s5\s500.*≈\s45.*10\s000/);
    expect(dialog.textContent).toContain('≈ по прогону черновика от 02.11.2026');
    expect(dialog.textContent).toContain('Изменения в настройке: 1');
  });

  it('ApproveDialog_StaleWarning', async () => {
    await openDialog({ '/api/clash/parts/p1/approve/preview': preview({ stale: true }) });

    expect(await screen.findByText('Черновик изменён после прогона — числа устарели')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Прогнать черновик' }).length).toBeGreaterThan(0);
  });

  it('ApproveDialog_NeverRun_NoTable', async () => {
    const dialog = await openDialog({ '/api/clash/parts/p1/approve/preview': preview({ neverRun: true, draftRunAt: null, pairs: [] }) });

    expect(await screen.findByText('Черновик ещё не прогонялся')).toBeTruthy();
    expect(dialog.querySelectorAll('[data-testid="preview-row"]')).toHaveLength(0);
  });

  it('ApproveDialog_FirstApproval_OnlyNew', async () => {
    const dialog = await openDialog({ '/api/clash/parts/p1/approve/preview': preview({ firstApproval: true, pairs: [{ pair: 'АР × ОВ', gone: 0, new: 120, same: 0 }] }) });

    expect(await screen.findByText(/Появятся ≈ 120/)).toBeTruthy();
    expect(dialog.querySelectorAll('[data-testid="preview-row"]')).toHaveLength(0);
  });

  it('ApproveDialog_NoScheduleHighlighted_SendsSchedules', async () => {
    await openDialog({
      '/api/clash/parts/p1/schedules': [schedule('s1', 'Ночная', false), schedule('s2', 'Дневная', false)],
      'POST /api/clash/parts/p1/approve': { version: 1, approvedAt: '2026-10-29T10:00:00Z', userName: 'Администратор', comment: null, runId: null },
    });

    expect(await screen.findByText('У части нет расписания — после утверждения её никто не прогонит')).toBeTruthy();
    fireEvent.click(screen.getByLabelText(/Ночная/));
    fireEvent.click(screen.getByRole('button', { name: 'Утвердить' }));

    await waitFor(() => expect(approvals()).toHaveLength(1));
    expect(JSON.parse(String(approvals()[0][1]!.body)).schedules).toEqual(['s1']);
  });

  it('ApproveDialog_NoClashSchedules_CreateLink', async () => {
    await openDialog({ '/api/clash/parts/p1/schedules': [] });

    expect((await screen.findByRole('link', { name: 'Создать расписание' })).getAttribute('href')).toBe('/schedules/new');
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Approve dialog after review', () => {
  it('ApproveDialog_NoScheduleHighlight_FollowsChoice', async () => {
    renderUi(<ApproveDialog partId="p1" nextVersion={2} dirty={false} onClose={() => {}} />, {
      '/api/clash/parts/p1/schedules': [schedule('s1', 'Ночная', true)],
      '/api/clash/parts/p1/approve/preview': preview(),
    });
    const hint = 'У части нет расписания — после утверждения её никто не прогонит';

    fireEvent.click(await screen.findByLabelText(/Ночная/));
    expect(await screen.findByText(hint)).toBeTruthy();
    fireEvent.click(screen.getByLabelText(/Ночная/));
    await waitFor(() => expect(screen.queryByText(hint)).toBeNull());
  });

  it('ApprovePreview_Dirty_WarnsAndSavesBeforeRun', async () => {
    const order: string[] = [];
    const save = vi.fn(async () => { order.push('save'); });
    renderUi(<ApprovePreviewBlock partId="p1" dirty onSave={save} />, {
      '/api/clash/parts/p1/approve/preview': preview(),
      'POST /api/clash/parts/p1/draft/run': () => { order.push('run'); return null; },
    });

    expect(await screen.findByText('Есть несохранённые правки — числа по прогону до них')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Прогнать черновик' }));
    await waitFor(() => expect(order).toEqual(['save', 'run']));
  });
});

describe('Approve guard', () => {
  it('Approve_DraftEqualsApproved_Disabled', async () => {
    await openDialog({ '/api/clash/parts/p1/approve/preview': preview({ changes: [] }) });

    expect(await screen.findByText(/^Черновик не отличается от утверждённой v\d+ — утверждать нечего$/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Утвердить' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('Approve_NeverRun_WarnsAndConfirms', async () => {
    await openDialog({
      '/api/clash/parts/p1/approve/preview': preview({ neverRun: true, draftRunAt: null, pairs: [] }),
      'POST /api/clash/parts/p1/approve': { version: 1, approvedAt: '2026-10-29T10:00:00Z', userName: 'Администратор', comment: null, runId: null },
    });

    expect(await screen.findByText('Черновик не прогонялся — последствия неизвестны')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Утвердить' }));
    expect(await screen.findByText('Черновик не прогонялся — последствия для базы коллизий неизвестны. Всё равно утвердить?')).toBeTruthy();
    expect(approvals()).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Да' }));
    await waitFor(() => expect(approvals()).toHaveLength(1));
  });
});
