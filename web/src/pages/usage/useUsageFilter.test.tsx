import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { filterQuery, readFilter, useUsageFilter } from './useUsageFilter';

function wrapperAt(path: string) {
  return ({ children }: { children: ReactNode }) => <MemoryRouter initialEntries={[path]}>{children}</MemoryRouter>;
}

describe('useUsageFilter', () => {
  it('defaults to 30 days without parameters', () => {
    expect(readFilter(new URLSearchParams())).toEqual({ period: '30d' });
    expect(filterQuery({ period: '30d' })).toBe('');
  });

  it('reads the filter from the address', () => {
    const { result } = renderHook(() => useUsageFilter(), {
      wrapper: wrapperAt('/usage/waits?period=7d&discipline=АР&machine=ws-01&revitYear=2024&tab=user'),
    });

    expect(result.current.filter).toEqual({ period: '7d', discipline: 'АР', machine: 'ws-01', revitYear: '2024' });
    expect(new URLSearchParams(result.current.query).get('discipline')).toBe('АР');
    expect(new URLSearchParams(result.current.query).get('from')).toBeTruthy();
  });

  it('writes the filter to the address and keeps other parameters', () => {
    const { result } = renderHook(() => ({ hook: useUsageFilter(), location: useLocation() }), { wrapper: wrapperAt('/usage/waits?tab=user') });

    act(() => result.current.hook.setFilter({ period: 'custom', from: '2026-11-01', to: '2026-11-10', user: 'u1' }));

    const params = new URLSearchParams(result.current.location.search);
    expect(params.get('tab')).toBe('user');
    expect(params.get('period')).toBe('custom');
    expect(params.get('from')).toBe('2026-11-01');
    expect(params.get('user')).toBe('u1');
    expect(result.current.hook.query).toBe('from=2026-11-01&to=2026-11-10&user=u1');
  });

  it('a preset period counts back from today, inclusive', () => {
    expect(filterQuery({ period: '7d' }, new Date(2026, 10, 24))).toBe('from=2026-11-18');
    expect(filterQuery({ period: '90d' }, new Date(2026, 10, 24))).toBe('from=2026-08-27');
  });

  it('custom dates are dropped when a preset is chosen', () => {
    expect(readFilter(new URLSearchParams('period=7d&from=2026-01-01'))).toEqual({ period: '7d' });
  });
});
