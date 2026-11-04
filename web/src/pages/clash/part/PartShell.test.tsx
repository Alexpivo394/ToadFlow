import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { focusManager } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { configuration, draft, ducts, partResponses, walls } from '../../../test/clashPart';
import { Failure, goBack, renderApp } from '../../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const puts = () => calls().filter(([url, init]) => url === '/api/clash/parts/p1/draft' && init?.method === 'PUT');

async function openSets(responses: Record<string, unknown> = partResponses) {
  renderApp('/clash/setup/parts/p1?tab=sets', responses);
  return (await screen.findByLabelText('Имя набора')) as HTMLInputElement;
}

/** Ответ GET черновика меняется, как после чужого сохранения, и страница его перечитывает. */
function serverSaves(next: unknown) {
  const fetchMock = vi.mocked(fetch);
  const original = fetchMock.getMockImplementation()!;
  fetchMock.mockImplementation(async (input, init) =>
    String(input) === '/api/clash/parts/p1/draft' && (init?.method ?? 'GET') === 'GET'
      ? new Response(JSON.stringify(next), { status: 200, headers: { 'Content-Type': 'application/json' } })
      : original(input, init));
  focusManager.setFocused(false);
  focusManager.setFocused(true);
}

describe('Part shell', () => {
  it('PartEditor_IncomingSave_KeepsLocalEdits', async () => {
    const name = await openSets();
    fireEvent.change(name, { target: { value: 'Стены наружные' } });
    expect(await screen.findByText('● есть несохранённые изменения')).toBeTruthy();

    serverSaves(draft(2, { ...configuration, sets: [walls, { ...ducts, name: 'Воздуховоды круглые' }] }));
    fireEvent.click(screen.getByText('ОВ'));

    expect(await screen.findByText('Воздуховоды круглые')).toBeTruthy();
    fireEvent.click(screen.getByText('АР'));
    expect(((await screen.findByLabelText('Имя набора')) as HTMLInputElement).value).toBe('Стены наружные');
    expect(screen.getByText('● есть несохранённые изменения')).toBeTruthy();
  });

  it('PartShell_SaveSendsBaseVersion', async () => {
    const name = await openSets({ ...partResponses, 'PUT /api/clash/parts/p1/draft': draft(2) });
    fireEvent.change(name, { target: { value: 'Стены наружные' } });

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    const body = JSON.parse(String(puts()[0][1]!.body));
    expect(body.baseVersion).toBe(1);
    expect(body.configuration.sets[0].name).toBe('Стены наружные');
  });

  it('PartShell_ConflictDialog_KeepMineResends', async () => {
    const current = draft(3, { ...configuration, sets: [{ ...walls, name: 'Стены А' }, ducts] });
    const name = await openSets({
      ...partResponses,
      'PUT /api/clash/parts/p1/draft': new Failure(409, 'Эти элементы уже изменил другой пользователь', {
        conflicts: [{ key: 'set:swalls', label: 'набор «Стены А» (АР)', kind: 'set' }], current,
      }),
    });
    fireEvent.change(name, { target: { value: 'Стены Б' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('набор «Стены А» (АР)')).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(puts()).toHaveLength(2));
    const resent = JSON.parse(String(puts()[1][1]!.body));
    expect(resent.baseVersion).toBe(3);
    expect(resent.configuration.sets[0].name).toBe('Стены Б');
  });

  it('PartShell_PresenceBadges', async () => {
    await openSets({ ...partResponses, '/api/clash/parts/p1/presence': [{ userId: 'u9', displayName: 'Иван', tab: 'sets', setId: 's-walls' }] });

    expect(await screen.findByText('Иван — наборы')).toBeTruthy();
    expect(await screen.findByText('Иван сейчас правит этот набор')).toBeTruthy();
    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/presence' && init?.method === 'POST')).toBe(true));
  });

  it('PartShell_DirtyGuardOnLeave', async () => {
    const name = await openSets();
    fireEvent.change(name, { target: { value: 'Стены наружные' } });

    fireEvent.click(screen.getByRole('button', { name: 'Части' }));

    expect(await screen.findByText('Есть несохранённые правки. Уйти без сохранения?')).toBeTruthy();
  });

  it('Draft_Dirty_NavigateViaMenu_AsksConfirm', async () => {
    const name = await openSets();
    fireEvent.change(name, { target: { value: 'Стены наружные' } });

    fireEvent.click(screen.getByRole('link', { name: 'Сводка' }));

    expect(await screen.findByText('Есть несохранённые правки. Уйти без сохранения?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Остаться' }));
    await waitFor(() => expect(screen.queryByText('Есть несохранённые правки. Уйти без сохранения?')).toBeNull());
    expect((screen.getByLabelText('Имя набора') as HTMLInputElement).value).toBe('Стены наружные');
  });

  it('Draft_Dirty_BrowserBack_AsksConfirm', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', partResponses, ['/clash/setup/parts']);
    const name = (await screen.findByLabelText('Имя набора')) as HTMLInputElement;
    fireEvent.change(name, { target: { value: 'Стены наружные' } });

    goBack();

    expect(await screen.findByText('Есть несохранённые правки. Уйти без сохранения?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Уйти' }));
    await waitFor(() => expect(screen.queryByLabelText('Имя набора')).toBeNull());
  });

  it('Draft_Clean_NavigatesFreely', async () => {
    await openSets();

    fireEvent.click(screen.getByRole('link', { name: 'Сводка' }));

    await waitFor(() => expect(screen.queryByLabelText('Имя набора')).toBeNull());
    expect(screen.queryByText('Есть несохранённые правки. Уйти без сохранения?')).toBeNull();
  });

  const approved = [
    { version: 3, approvedAt: '2026-10-29T10:00:00Z', userName: 'Иванов', comment: 'допуск 50', runId: null },
    { version: 2, approvedAt: '2026-10-28T10:00:00Z', userName: 'Петров', comment: null, runId: null },
  ];

  it('PartShell_ShowsApprovedVersion', async () => {
    await openSets({ ...partResponses, '/api/clash/parts/p1/versions': approved });

    // Значок — коротко «v3», кто и когда утвердил — в подсказке.
    expect((await screen.findByLabelText('Утверждена версия 3')).textContent).toBe('v3');
    fireEvent.click(screen.getByRole('button', { name: 'Версии' }));
    expect(await screen.findByText('допуск 50')).toBeTruthy();
    expect(screen.getByText('v2')).toBeTruthy();
  });

  it('PartShell_NotApproved_SaysSo', async () => {
    await openSets();

    expect(await screen.findByText('Не утверждена')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Сбросить черновик к утверждённой' })).toBeNull();
  });

  it('PartShell_ResetDraft_Confirms', async () => {
    await openSets({ ...partResponses, '/api/clash/parts/p1/versions': approved, 'POST /api/clash/parts/p1/draft/reset': draft(5) });

    fireEvent.click(await screen.findByRole('button', { name: 'Сбросить черновик к утверждённой' }));
    expect(await screen.findByText('Заменить черновик утверждённой версией 3? Правки черновика пропадут.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Да' }));

    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/draft/reset' && init?.method === 'POST')).toBe(true));
  });

  it('PartShell_404_ShowsReason', async () => {
    renderApp('/clash/setup/parts/p1', { ...partResponses, '/api/clash/parts/p1': new Failure(404, 'Часть не найдена — её удалили') });

    expect(await screen.findByText('Часть не найдена — её удалили')).toBeTruthy();
    expect(screen.getByText('Проверка не открылась')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Повторить' })).toBeTruthy();
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Part shell: navigation', () => {
  it('PartShell_TemplateMenu_ApplyOrSave', async () => {
    await openSets();

    fireEvent.click(screen.getByRole('button', { name: 'Шаблон ▾' }));
    expect(await screen.findByText('Применить шаблон')).toBeTruthy();
    expect(screen.getByText('Сохранить как шаблон')).toBeTruthy();
  });
});
