import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { partResponses } from '../../../test/clashPart';
import { renderApp } from '../../../test/renderApp';

// WebGL в jsdom нет: вместо сцены three.js — заглушка с номером коллизии.
vi.mock('../viewer/ClashViewer', () => ({ default: (props: { scene: { number: number } }) => <div>сцена {props.scene.number}</div> }));

const hit = (n: number, check: string, extra: object = {}) => ({
  runId: 'r1', number: n, depthMm: 100 - n, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', setNamesA: ['Стены'], categoryA: 'OST_Walls',
  familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', setNamesB: ['Воздуховоды'],
  categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: [check], setPairKey: 'swalls|sducts', ...extra,
});

const checks = [
  { key: 'k-arar', groupA: 'g-ar', groupAName: 'АР', groupB: 'g-ar', groupBName: 'АР', count: 0, toleranceMm: 20 },
  { key: 'k-arov', groupA: 'g-ar', groupAName: 'АР', groupB: 'g-ov', groupBName: 'ОВ', count: 51, toleranceMm: 20 },
  { key: 'k-ovov', groupA: 'g-ov', groupAName: 'ОВ', groupB: 'g-ov', groupBName: 'ОВ', count: 1, toleranceMm: 50 },
];

// Страница по параметрам: в «АР × ОВ» 51 коллизия (1–50 на первой странице, 51 на второй), в «ОВ × ОВ» одна — № 77.
function hits(url: string) {
  const query = new URL(url, 'http://x').searchParams;
  const check = query.get('check');
  const page = Number(query.get('page') ?? 1);
  if (check === 'k-ovov') return { total: 1, items: [hit(77, 'k-ovov')] };
  if (check !== 'k-arov') return { total: 0, items: [] };
  return page === 1 ? { total: 51, items: Array.from({ length: 50 }, (_, i) => hit(i + 1, 'k-arov')) } : { total: 51, items: [hit(51, 'k-arov')] };
}

const scene = (url: string) => {
  const number = Number(url.split('/hits/')[1].split('/')[0]);
  return { runId: 'r1', number, x: 0, y: 0, z: 0, radiusMm: 5000, depthMm: 30, elements: [], meshTriangles: 0, truncated: false };
};

const succeeded = { runId: 'r1', state: 'Succeeded', stage: null, hitCount: 52, error: null, createdAt: '2026-10-28T10:00:00Z', finishedAt: '2026-10-28T10:01:00Z', stats: null };

const responses = {
  ...partResponses,
  '/api/clash/parts/p1/draft/run': succeeded,
  '/api/clash/parts/p1/draft/summary': { total: 52, checks, legacy: false },
  '/api/clash/parts/p1/draft/hits': hits,
  '/api/clash/parts/p1/draft/facets': {
    levels: [{ value: 'Этаж 2', count: 30 }, { value: 'Этаж 1', count: 21 }], models: [{ value: 'POL_K1_AR.rvt', count: 51 }],
    categories: [{ value: 'OST_Walls', count: 51 }],
  },
  '/api/clash/parts/p1/draft/checks/k-arov/setpairs': [{ setA: 's-walls', setAName: 'Стены', setB: 's-ducts', setBName: 'Воздуховоды', count: 51 }],
  '/api/clash/parts/p1/draft/checks/k-ovov/setpairs': [],
  ...Object.fromEntries([1, 2, 50, 51, 77].map((n) => [`/api/clash/runs/r1/hits/${n}/scene`, scene])),
  ...Object.fromEntries([1, 2, 50, 51, 77].map((n) => [`/api/clash/runs/r1/hits/${n}/meshes`, new Uint32Array([0x31564354, 0]).buffer])),
};

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

const open = (query = '') => renderApp(`/clash/setup/parts/p1?tab=draft${query}`, responses);

describe('Draft tab', () => {
  it('DraftTab_ChecksInMatrixOrderWithZeros', async () => {
    open();

    const arAr = (await screen.findAllByText((_, el) => el?.textContent === 'АР × АР 20'))[0];
    expect(arAr).toBeTruthy();
    expect(screen.getAllByText((_, el) => el?.textContent === 'ОВ × ОВ 50').length).toBeGreaterThan(0);
    expect(screen.getByText('0')).toBeTruthy();
    expect(screen.getAllByText('51').length).toBeGreaterThan(0);
  });

  it('DraftTab_SelectCheckShowsSetPairGroups', async () => {
    open();

    expect(await screen.findByText('▾ Стены × Воздуховоды', {}, { timeout: 5000 })).toBeTruthy();
    expect(await screen.findByText('сцена 1', {}, { timeout: 5000 })).toBeTruthy(); // первая коллизия первой непустой проверки
    fireEvent.click(screen.getAllByText((_, el) => el?.textContent === 'ОВ × ОВ 50')[0]);
    expect(await screen.findByText('сцена 77', {}, { timeout: 5000 })).toBeTruthy();
  });

  it('DraftTab_ArrowDownCrossesPageAndCheck', async () => {
    open('&check=k-arov&hit=50');
    expect(await screen.findByText('сцена 50', {}, { timeout: 5000 })).toBeTruthy();

    fireEvent.keyDown(window, { key: 'ArrowDown' });
    expect(await screen.findByText('сцена 51', {}, { timeout: 5000 })).toBeTruthy();

    fireEvent.keyDown(window, { key: 'ArrowDown' });
    expect(await screen.findByText('сцена 77', {}, { timeout: 5000 })).toBeTruthy();

    fireEvent.keyDown(window, { key: 'ArrowUp' });
    expect(await screen.findByText('сцена 51', {}, { timeout: 5000 })).toBeTruthy();
  });

  it('DraftTab_FacetsAsDropdowns', async () => {
    open();
    await screen.findByText('сцена 1');

    fireEvent.click(screen.getAllByLabelText('Уровень')[0]);
    fireEvent.click(await screen.findByRole('option', { name: /Этаж 2 — 30/ }));

    await waitFor(() => expect(calls().some(([url]) => url.includes('/draft/hits?') && url.includes('level=%D0%AD%D1%82%D0%B0%D0%B6+2'))).toBe(true));
  });

  it('DraftTab_UrlKeepsCheckAndHit', async () => {
    open('&check=k-ovov&hit=77');

    expect(await screen.findByText('сцена 77', {}, { timeout: 5000 })).toBeTruthy();
    expect(calls().some(([url]) => url.startsWith('/api/clash/runs/r1/hits/77/scene'))).toBe(true);
  });

  it('DraftTab_LegacyResultBanner', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', { ...responses, '/api/clash/parts/p1/draft/summary': { total: 10, checks: [], legacy: true } });

    expect(await screen.findByText(/прогоните черновик заново/)).toBeTruthy();
  });

  it('DraftTab_ApproveEnabledAndRunStarts', async () => {
    open();

    const approve = (await screen.findByRole('button', { name: 'Утвердить…' })) as HTMLButtonElement;
    expect(approve.disabled).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Прогнать' }));
    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/draft/run' && init?.method === 'POST')).toBe(true));
  });

  it('DraftTab_FailedRunShowsError', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', {
      ...responses,
      '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Failed', error: 'слепок модели повреждён' },
    });

    expect(await screen.findByText('слепок модели повреждён')).toBeTruthy();
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Draft on the shared filter bar', () => {
  it('Draft_SearchByNumber', async () => {
    open();
    await screen.findByText('сцена 1');

    const search = screen.getByLabelText('Поиск');
    fireEvent.change(search, { target: { value: '77' } });
    fireEvent.keyDown(search, { key: 'Enter' });

    expect(await screen.findByText('сцена 77', {}, { timeout: 5000 })).toBeTruthy(); // номер прогона, даже не на этой странице
  });

  it('Draft_GroupSetPairDefault', async () => {
    open();

    expect(await screen.findByText('▾ Стены × Воздуховоды', {}, { timeout: 5000 })).toBeTruthy();
    const group = () => screen.getAllByLabelText('Группировка').filter((e) => e.tagName === 'INPUT').map((e) => (e as HTMLInputElement).value);
    expect(group()).toContain('По парам наборов');
    expect(calls().find(([url]) => url.includes('/draft/hits?'))![0]).toContain('sort=setpair');

    fireEvent.click(screen.getAllByLabelText('Группировка')[0]);
    fireEvent.click(await screen.findByRole('option', { name: 'Без группировки' }));

    await waitFor(() => expect(screen.queryByText('▾ Стены × Воздуховоды')).toBeNull());
  });
});

describe('Draft run line', () => {
  it('Draft_RunLine_TimeVisible_StatsInTooltip', async () => {
    const stats = { elements: 1234, triangles: 5678, loadSeconds: 3, broadSeconds: 1, narrowSeconds: 1, peakWorkingSetBytes: 1048576 };
    renderApp('/clash/setup/parts/p1?tab=draft', { ...responses, '/api/clash/parts/p1/draft/run': { ...succeeded, stats } });

    const line = await screen.findByText(/^запущен/);
    expect(screen.queryByText(/Элементов/)).toBeNull();
    fireEvent.mouseEnter(line);
    expect(await screen.findByText(/Элементов 1\s234/)).toBeTruthy();
    expect(screen.getByText(/52 коллизий/)).toBeTruthy();
  });
});

describe('Draft run state', () => {
  it('Draft_Running_NoEmptyHint_RunDisabled', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', {
      ...responses,
      '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Running', stage: 'поиск', finishedAt: null, hitCount: null },
      '/api/clash/parts/p1/draft/summary': { total: 0, checks: [], legacy: false },
    });

    expect(await screen.findByText('Идёт прогон…')).toBeTruthy();
    expect(screen.queryByText('Коллизий пока нет — нажмите «Прогнать»')).toBeNull();
    expect((screen.getByRole('button', { name: 'Прогнать' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('RunPanel_QueuedShowsPositionAndTypical', async () => {
    renderApp('/clash/setup/parts/p1?tab=draft', {
      ...responses,
      '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Queued', finishedAt: null, hitCount: null, queuePosition: 3, typicalSeconds: 118 },
    });

    expect(await screen.findByText('В очереди: 3-й · обычно ≈ 2 мин')).toBeTruthy();
  });

  it('RunLine_EngineSilent5Min_Warning', async () => {
    const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
    renderApp('/clash/setup/parts/p1?tab=draft', {
      ...responses,
      '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Queued', finishedAt: null, hitCount: null, queuePosition: 1 },
      '/api/clash/engine/status': { lastSeenAt: minutesAgo(12), serverStartedAt: minutesAgo(60), queued: 1 },
    });

    expect(await screen.findByText(/Движок не отвечает 12 мин/)).toBeTruthy();
  });

  it('RunLine_EngineAlive_NoWarning', async () => {
    const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();
    renderApp('/clash/setup/parts/p1?tab=draft', {
      ...responses,
      '/api/clash/parts/p1/draft/run': { ...succeeded, state: 'Queued', finishedAt: null, hitCount: null, queuePosition: 1 },
      '/api/clash/engine/status': { lastSeenAt: minutesAgo(1), serverStartedAt: minutesAgo(60), queued: 1 },
    });

    expect(await screen.findByText(/В очереди: 1-й/)).toBeTruthy();
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(screen.queryByText(/Движок не отвечает/)).toBeNull();
  });
});
