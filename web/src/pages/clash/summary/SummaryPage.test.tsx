import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderApp } from '../../../test/renderApp';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;
const analytics = () => calls().filter(([url]) => url.startsWith('/api/clash/analytics')).map(([url]) => new URL(url, 'http://x').searchParams);

const check = (partId: string, name: string, object: string, disciplines: string[]) => ({
  partId, name, object, part: null, version: 1, lastRunAt: null, lastRun: null, open: 5,
  models: disciplines.map((d, i) => ({ modelId: `${partId}m${i}`, name: `${name}_${d}.rvt`, discipline: d })),
});

const view = (lagging: object[]) => ({
  from: '2026-10-01', to: '2026-10-31',
  current: {
    open: 120, fake: 30, fakeShare: 0.2, undecided: 30,
    byDiscipline: [{ discipline: 'ОВК', assigned: 60, undecided: 25 }, { discipline: 'КР', assigned: 20, undecided: 30 }],
    ageBuckets: [{ label: 'до 7 дней', count: 50 }, { label: '7–30 дней', count: 40 }, { label: '30–90 дней', count: 20 }, { label: 'больше 90 дней', count: 10 }],
  },
  period: { new: 40, fixed: 25, returned: 3 },
  series: [{ date: '2026-10-30', open: 110 }, { date: '2026-10-31', open: 120 }],
  lagging,
  stuckDays: 14,
});

const responses = {
  '/api/clash/checks': [check('p1', 'Корпус 1', 'DON', ['КР', 'ОВК']), check('p2', 'Корпус 2', 'POL', ['АР', 'ВК'])],
  '/api/clash/analytics': (url: string) => new URL(url, 'http://x').searchParams.get('groupBy') === 'part'
    ? view([{ key: 'Корпус 1', open: 80, growth: 10, stuck: 4, partId: 'p1' }])
    : view([{ key: 'ОВК', open: 90, growth: 12, stuck: 6, partId: null }, { key: 'КР', open: 30, growth: -2, stuck: 0, partId: null }]),
};

describe('Clash summary', () => {
  it('Summary_FiltersGoToQuery', async () => {
    renderApp('/clash/summary', responses);

    expect(await screen.findByText('120')).toBeTruthy(); // активных
    expect(screen.getByText('20 %')).toBeTruthy(); // доля фиктивных
    fireEvent.click(screen.getAllByLabelText('Проект').find((e) => (e as HTMLInputElement).type !== 'hidden')!);
    fireEvent.click(await screen.findByRole('option', { name: 'DON' }));
    fireEvent.change(screen.getAllByLabelText('Раздел').find((e) => (e as HTMLInputElement).type !== 'hidden')!, { target: { value: 'ОВК' } });
    fireEvent.click(screen.getByRole('radio', { name: 'Неделя' }));

    await waitFor(() => expect(analytics().some((p) => p.get('object') === 'DON' && p.get('discipline') === 'ОВК' && p.get('from') !== null)).toBe(true));
  });

  it('Summary_LoadByDiscipline', async () => {
    renderApp('/clash/summary', responses);

    const table = await screen.findByRole('table', { name: 'На ком коллизии' });
    expect(screen.getByText('Не решено, кто правит: 30 из 120')).toBeTruthy();
    const ovk = within(table).getByText('ОВК').closest('tr') as HTMLElement;
    expect(within(ovk).getByText('60')).toBeTruthy();
    expect(within(ovk).getByText('25')).toBeTruthy();
    expect(screen.queryByText('Кто правит')).toBeNull();
    expect(screen.queryByText(/сторона/)).toBeNull();
  });

  it('Summary_LaggingRowOpensList', async () => {
    renderApp('/clash/summary', responses);

    fireEvent.click(await screen.findByRole('link', { name: 'ОВК' }));

    expect(await screen.findByRole('heading', { name: 'Проверки' })).toBeTruthy();
    expect(await screen.findByText('Корпус 1')).toBeTruthy();
    expect(screen.queryByText('Корпус 2')).toBeNull(); // нет моделей ОВК
    expect(screen.getByText(/Раздел: ОВК/)).toBeTruthy();
  });
});

vi.setConfig({ testTimeout: 15_000 });

describe('Clash summary: clearer', () => {
  it('Summary_AllInFilters_HintsAndNoInnerScroll', async () => {
    renderApp('/clash/summary', responses);

    expect(await screen.findByText('за период, включая уже закрытые')).toBeTruthy();
    const input = (label: string) => screen.getAllByLabelText(label).find((e) => e.tagName === 'INPUT') as HTMLInputElement;
    expect([input('Проект').placeholder, input('Проверка').placeholder]).toEqual(['Все', 'Все']);
    const load = screen.getByRole('table', { name: 'На ком коллизии' });
    expect(load.closest('.mantine-ScrollArea-root')).toBeNull();

    fireEvent.mouseEnter(screen.getByText('Висят'));
    expect(await screen.findByText('Открыты дольше 14 дней без движения')).toBeTruthy();
  });
});
