import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderUi } from '../../../test/renderApp';
import { RunsDrawer } from './RunsDrawer';

const run = (runId: string, finishedAt: string, comparable = true) => ({
  runId, partId: 'p1', version: 1, finishedAt, trigger: 'manual', startedBy: null, searchSeconds: 10, summary: null, comparable,
});

const runs = [run('r3', '2026-11-03T10:00:00Z'), run('r2', '2026-11-02T10:00:00Z'), run('r1', '2026-10-01T10:00:00Z', false)];

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

describe('Runs drawer', () => {
  it('Runs_CompareTwo_Table', async () => {
    renderUi(<RunsDrawer partId="p1" opened onClose={() => {}} />, {
      '/api/clash/parts/p1/approved/runs': runs,
      '/api/clash/parts/p1/approved/runs/compare': {
        runA: 'r2', runB: 'r3',
        rows: [
          { pair: 'АР × ОВ', foundA: 100, foundB: 90, new: 5, returned: 1, fixed: 14, goneByRules: 2 },
          { pair: 'АР × КР', foundA: 10, foundB: 12, new: 2, returned: 0, fixed: 0, goneByRules: 0 },
        ],
      },
    });

    const compare = await screen.findByRole('button', { name: 'Сравнить' }) as HTMLButtonElement;
    expect(compare.disabled).toBe(true);
    fireEvent.click(screen.getAllByRole('checkbox')[0]);
    expect(compare.disabled).toBe(true);
    fireEvent.click(screen.getAllByRole('checkbox')[1]);
    expect(compare.disabled).toBe(false);
    fireEvent.click(compare);

    await waitFor(() => expect(calls().some(([url]) => url.startsWith('/api/clash/parts/p1/approved/runs/compare?') && url.includes('a=r3') && url.includes('b=r2'))).toBe(true));
    const table = await screen.findByTestId('run-compare');
    expect(table.textContent).toContain('Появилось');
    expect(table.textContent).toContain('Ушло из-за правил');
    const total = screen.getByTestId('run-compare-total');
    expect(total.textContent).toMatch(/Итого\s*110\s*102\s*7\s*1\s*14\s*2/);
  });

  it('Runs_OldRunNotComparable', async () => {
    renderUi(<RunsDrawer partId="p1" opened onClose={() => {}} />, { '/api/clash/parts/p1/approved/runs': runs });

    await screen.findByRole('button', { name: 'Сравнить' });
    const boxes = screen.getAllByRole('checkbox') as HTMLInputElement[];
    expect(boxes.map((b) => b.disabled)).toEqual([false, false, true]);
    expect(screen.getByText('Прогон до обновления — сравнение недоступно')).toBeTruthy();
  });
});
