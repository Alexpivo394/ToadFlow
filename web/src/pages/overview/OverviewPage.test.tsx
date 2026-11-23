import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../../test/renderApp';

const server = {
  version: '3.0.0', startedAt: '2026-11-10T10:00:00Z', databaseBytes: 4.2 * 1024 ** 3,
  volumes: [{ name: 'Сервер', freeBytes: 380 * 1024 ** 3, totalBytes: 1000 * 1024 ** 3 }], processMemoryBytes: 900 * 1024 ** 2,
  workers: [{ module: 'clash', title: 'Движок коллизий', lastSeenAt: '2026-11-21T09:59:00Z', online: true }],
};

const series = Array.from({ length: 14 }, (_, i) => i);

const tiles = [
  { module: 'export', value: '142', label: 'за сутки', trend: null, lines: ['ошибок: 3', 'следующий запуск 21.11 22:00'], link: '/export/schedules', series, highlight: 'выгружено 142 модели' },
  { module: 'loi', value: '—', label: 'нет данных', trend: null, lines: [], link: '/loi', series: null, highlight: null },
  { module: 'clash', value: '318', label: 'открыто', trend: null, lines: ['новых за неделю: 24'], link: '/clash/summary', series, highlight: 'закрыто 12 коллизий' },
];

const hourly = Array.from({ length: 24 }, (_, i) => ({ hour: new Date(Date.UTC(2026, 10, 21, i)).toISOString(), succeeded: i % 3, warning: 0, failed: i === 5 ? 1 : 0 }));
const pulse = { doneDay: 97, failedDay: 3, queued: 12, revitBusy: 5, revitTotal: 6, hourly };

const overview = (attention: object[] = [], p: object = pulse) => ({ attention, tiles, server, pulse: p });

const agents = [
  {
    id: 'a1', machineName: 'BIM-PC-01', ip: null, agentVersion: '1.0.3', totalRamMb: 65536, state: 'Online', isDraining: false,
    lastSeenAt: new Date().toISOString(), cpuPercent: 64, ramUsedMb: 41000, revits: [{ year: 2022, hasNwcExporter: true }],
    limits: [{ year: 2022, maxInstances: 2 }], isOutdated: false,
    instances: [
      { slot: 1, year: 2022, pid: 1, state: 'Busy', jobId: 'j9', stage: 'Экспорт NWC', stageSince: new Date(Date.now() - 12 * 60_000).toISOString(), modelName: 'ЖК-Север_АР_К1.rvt' },
      { slot: 2, year: 2022, pid: 2, state: 'Idle', jobId: null, stage: null, stageSince: null, modelName: null },
    ],
  },
];

const feed = [
  { at: '2026-11-21T09:41:00Z', module: 'export', text: 'ЖК_Север_К1_КР.rvt — готово · BIM-PC-01', tone: 'ok' },
  { at: '2026-11-21T09:05:00Z', module: 'export', text: 'Школа_АР.rvt — ошибка', tone: 'fail' },
];

const responses = (attention: object[] = [], p: object = pulse) => ({
  '/api/overview': overview(attention, p),
  '/api/overview/feed': feed,
  '/api/agents': agents,
  '/api/settings': { silenceTimeoutMinutes: 10 },
});

const operator = { id: 'u2', login: 'op', displayName: 'Иванов', role: 'Operator', isDisabled: false, lastLoginAt: null };

describe('OverviewPage', () => {
  it('hero greets by name and tells the day in one phrase with key numbers', async () => {
    renderApp('/', { ...responses(), '/api/auth/me': operator });

    const hero = await screen.findByRole('region', { name: 'Сводка' });
    expect(await within(hero).findByText(/, Иванов\. Всё спокойно/)).toBeTruthy();
    expect(within(hero).getByText('За сутки: выгружено 142 модели, закрыто 12 коллизий.')).toBeTruthy();
    expect(within(hero).getByText('5 / 6')).toBeTruthy();
    expect(within(hero).getByText('100')).toBeTruthy();
    expect(within(hero).getByText('97 %')).toBeTruthy();
    expect(within(hero).getByText('12')).toBeTruthy();
    expect(hero.getAttribute('data-tone')).toBe('calm');
  });

  it('hero turns to problem tone and shows a dash, not NaN, on an empty day', async () => {
    renderApp('/', responses([{ level: 'Problem', text: 'Агент BIM-PC-03 не в сети 2 ч', link: '/farm/agents', module: null, minRole: 'Viewer' }],
      { ...pulse, doneDay: 0, failedDay: 0 }));

    const hero = await screen.findByRole('region', { name: 'Сводка' });
    expect(await within(hero).findByText(/1 сбой — посмотрите ниже/)).toBeTruthy();
    expect(hero.getAttribute('data-tone')).toBe('problem');
    expect(within(hero).getByText('—')).toBeTruthy();
    expect(hero.textContent).not.toContain('NaN');
  });

  it('shows "Всё в порядке" when there is nothing to attend', async () => {
    renderApp('/', responses());

    expect(await screen.findByText('Всё в порядке')).toBeTruthy();
  });

  it('attention chips link to their place; problems come before todos', async () => {
    renderApp('/', responses([
      { level: 'Todo', text: '2 заявки на регистрацию', link: '/admin/users', module: null, minRole: 'Operator' },
      { level: 'Problem', text: 'Агент BIM-PC-03 не в сети 2 ч', link: '/farm/agents', module: null, minRole: 'Viewer' },
    ]));

    const block = await screen.findByRole('region', { name: 'Требует внимания' });
    const links = await within(block).findAllByRole('link');
    expect(links.map((l) => [l.textContent, l.getAttribute('href'), l.getAttribute('data-level')])).toEqual([
      ['Агент BIM-PC-03 не в сети 2 ч', '/farm/agents', 'Problem'],
      ['2 заявки на регистрацию', '/admin/users', 'Todo'],
    ]);
  });

  const failedLoi = { level: 'Problem', text: 'Проверка LOI: 2 модели с ошибкой за сутки', link: '/loi/history?status=Failed', module: 'loi', minRole: 'Viewer', resolveKey: 'loi' };
  const offline = { level: 'Problem', text: 'Агент BIM-PC-03 не в сети 2 ч', link: '/farm/agents', module: null, minRole: 'Viewer', resolveKey: null };
  type FetchMock = { mock: { calls: [string, RequestInit?][] } };
  const posts = (path: string) => (fetch as unknown as FetchMock).mock.calls.filter(([url, init]) => url === path && init?.method === 'POST');

  it('coordinator marks failed models resolved; rows that clear by themselves have no button', async () => {
    renderApp('/', { ...responses([failedLoi, offline]), '/api/auth/me': operator, 'POST /api/overview/attention/loi/resolve': null });

    const block = await screen.findByRole('region', { name: 'Требует внимания' });
    const buttons = await within(block).findAllByRole('button', { name: 'Решено' });
    expect(buttons).toHaveLength(1);
    fireEvent.click(buttons[0]);

    const dialog = await screen.findByRole('dialog', { name: 'Проверка LOI: 2 модели с ошибкой за сутки' });
    const submit = within(dialog).getByRole('button', { name: 'Отметить решённым' });
    expect((submit as HTMLButtonElement).disabled).toBe(true); // без примечания нельзя
    fireEvent.change(within(dialog).getByLabelText('Почему решено'), { target: { value: 'Перезапустили RS22' } });
    fireEvent.click(submit);
    await waitFor(() => expect(posts('/api/overview/attention/loi/resolve')).toHaveLength(1));
    expect(JSON.parse(String(posts('/api/overview/attention/loi/resolve')[0][1]?.body))).toEqual({ note: 'Перезапустили RS22' });
  });

  it('coordinator resolves followed-schedule errors right in For you', async () => {
    renderApp('/', {
      ...responses(), '/api/auth/me': operator, 'POST /api/overview/attention/export/resolve': null,
      '/api/me/today': { items: [
        { module: null, text: 'Выгрузка: расписания с ошибками за сутки', link: '/export/history', tone: 'Action', count: 1, resolveKey: 'export' },
        { module: 'clash', text: 'Новые коллизии в ваших разделах', link: '/clash/my?since=unseen', tone: 'Action', count: 4, resolveKey: null },
      ] },
    });

    const block = await screen.findByRole('region', { name: 'Для вас' });
    const buttons = await within(block).findAllByRole('button', { name: 'Решено' });
    expect(buttons).toHaveLength(1);
    fireEvent.click(buttons[0]);
    const dialog = await screen.findByRole('dialog', { name: 'Выгрузка: расписания с ошибками за сутки' });
    fireEvent.change(within(dialog).getByLabelText('Почему решено'), { target: { value: 'Модель удалили' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Отметить решённым' }));
    await waitFor(() => expect(posts('/api/overview/attention/export/resolve')).toHaveLength(1));
  });

  it('viewer sees failed models but no resolve button', async () => {
    renderApp('/', { ...responses([failedLoi]), '/api/auth/me': { ...operator, role: 'Viewer' } });

    const block = await screen.findByRole('region', { name: 'Требует внимания' });
    expect(await within(block).findByText('Проверка LOI: 2 модели с ошибкой за сутки')).toBeTruthy();
    expect(within(block).queryByRole('button', { name: 'Решено' })).toBeNull();
  });

  it('module tiles link to the module and draw their two-week chart', async () => {
    renderApp('/', responses());

    const main = await screen.findByRole('main');
    const exportTile = await within(main).findByRole('link', { name: /Выгрузка/ });
    expect(exportTile.getAttribute('href')).toBe('/export/schedules');
    expect(within(exportTile).getByText('142')).toBeTruthy();
    expect(within(exportTile).getByText('ошибок: 3')).toBeTruthy();
    expect(within(exportTile).getByTestId('spark-export')).toBeTruthy();

    const loiTile = within(main).getByRole('link', { name: /Проверка LOI/ });
    expect(loiTile.getAttribute('data-muted')).toBe('true');
    expect(within(loiTile).queryByTestId('spark-loi')).toBeNull();
  });

  it('agents now: busy Revit shows model and elapsed time, free one says so, no kill buttons here', async () => {
    renderApp('/', { ...responses(), '/api/auth/me': operator });

    const block = await screen.findByRole('region', { name: 'Агенты прямо сейчас' });
    expect(await within(block).findByText('ЖК-Север_АР_К1.rvt')).toBeTruthy();
    expect(within(block).getByText(/идёт 12 мин/)).toBeTruthy();
    expect(within(block).getByText('свободен')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Убить' })).toBeNull();
  });

  it('jobs by hour draws 24 hours', async () => {
    renderApp('/', responses());

    const block = await screen.findByRole('region', { name: 'Задания за сутки' });
    expect(within(block).getAllByTestId('hour')).toHaveLength(24);
  });

  it('server card shows disk usage, version and workers', async () => {
    renderApp('/', responses());

    const block = await screen.findByRole('region', { name: 'Сервер' });
    expect(await within(block).findByText('62 %')).toBeTruthy();
    expect(within(block).getByText(/3\.0\.0/)).toBeTruthy();
    expect(within(block).getByText(/Движок коллизий/)).toBeTruthy();
  });

  it('live feed lists what happened, newest first', async () => {
    renderApp('/', responses());

    const block = await screen.findByRole('region', { name: 'Что происходит' });
    const rows = await within(block).findAllByTestId('feed-item');
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringContaining('ЖК_Север_К1_КР.rvt — готово'), expect.stringContaining('Школа_АР.rvt — ошибка')]);
  });

  it('never says "ферма"', async () => {
    renderApp('/', responses());

    const main = await screen.findByRole('main');
    await within(main).findByText('ЖК-Север_АР_К1.rvt');
    expect(main.textContent ?? '').not.toMatch(/ферм/i);
  });
});
