import { act, renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { toQuery, useListParams, type ListParams } from './useListParams';

let location = '';
function Probe() {
  const l = useLocation();
  location = l.search;
  return null;
}

const wrapper = (url = '/') => ({ children }: { children: ReactNode }) =>
  createElement(MemoryRouter, { initialEntries: [url] }, createElement(Probe), children);

const base: ListParams = { status: 'open', sort: 'depth', group: 'none', page: 1 };

afterEach(() => {
  vi.restoreAllMocks();
  try { localStorage.clear(); } catch { /* нет хранилища */ }
});

describe('useListParams', () => {
  it('ListParams_DefaultsNotInUrl', () => {
    const { result } = renderHook(() => useListParams({ sort: 'depth' }), { wrapper: wrapper() });
    act(() => result.current[1]({ pair: 'КР × ОВК' }));
    act(() => result.current[1]({ sort: 'depth', pair: undefined }));
    expect(location).toBe('');
    act(() => result.current[1]({ sort: 'number' }));
    expect(new URLSearchParams(location).get('sort')).toBe('number');
    expect(new URLSearchParams(location).has('status')).toBe(false);
  });

  it('ListParams_ChangeResetsPage', () => {
    const { result } = renderHook(() => useListParams({}), { wrapper: wrapper('/?page=3&level=Этаж%201') });
    expect(result.current[0].page).toBe(3);
    expect(result.current[0].level).toBe('Этаж 1');
    act(() => result.current[1]({ page: 4 }));
    expect(result.current[0].page).toBe(4);
    act(() => result.current[1]({ status: 'closed' }));
    expect(result.current[0].page).toBe(1);
    expect(new URLSearchParams(location).has('page')).toBe(false);
  });

  it('ListParams_RestoresFromStorage_WhenUrlEmpty', () => {
    localStorage.setItem('test.list', JSON.stringify({ status: 'all', pair: 'АР × КР', page: 5 }));
    const { result } = renderHook(() => useListParams({}, 'test.list'), { wrapper: wrapper() });
    expect(result.current[0]).toMatchObject({ status: 'all', pair: 'АР × КР', page: 1 });

    act(() => result.current[1]({ level: 'Этаж 2' }));
    expect(JSON.parse(localStorage.getItem('test.list') ?? '{}')).toMatchObject({ status: 'all', pair: 'АР × КР', level: 'Этаж 2' });

    const fromUrl = renderHook(() => useListParams({}, 'test.list'), { wrapper: wrapper('/?sort=number') });
    expect(fromUrl.result.current[0]).toMatchObject({ status: 'open', sort: 'number' }); // адрес важнее сохранённого
  });

  it('ListParams_PickDefault_OverStored_TakesEffect', () => {
    localStorage.setItem('test.list', JSON.stringify({ assignee: 'taken' }));
    const { result } = renderHook(() => useListParams({ assignee: 'mine' }, 'test.list'), { wrapper: wrapper() });
    expect(result.current[0].assignee).toBe('taken');
    act(() => result.current[1]({ assignee: 'mine' }));
    expect(result.current[0].assignee).toBe('mine');
  });

  it('ListParams_StorageThrows_StillWorks', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('запрещено'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('запрещено'); });
    const { result } = renderHook(() => useListParams({}, 'test.list'), { wrapper: wrapper() });
    expect(result.current[0]).toMatchObject(base);
    act(() => result.current[1]({ q: '123' }));
    expect(result.current[0].q).toBe('123');
  });
});

describe('toQuery', () => {
  it('toQuery_OpenPresetExpands', () => {
    const open = new URLSearchParams(toQuery({ ...base, pair: 'КР × ОВК' }));
    expect(open.get('status')).toBe('New,Active,Returned,FixedPending,Unchecked');
    expect(open.get('pair')).toBe('КР × ОВК');
    expect(open.get('sort')).toBe('depth');
    expect(open.has('group')).toBe(false);

    expect(new URLSearchParams(toQuery({ ...base, status: 'closed' })).get('status')).toBe('Fixed,GoneByRules,Fake');
    expect(new URLSearchParams(toQuery({ ...base, status: 'all' })).get('status')).toBe('all');
    expect(new URLSearchParams(toQuery({ ...base, status: 'Fake,Fixed' })).get('status')).toBe('Fake,Fixed');

    const mine = new URLSearchParams(toQuery({ ...base, assignee: 'mine', group: 'element', side: 'ОВК', page: 2 }));
    expect([mine.get('scope'), mine.get('group'), mine.get('side'), mine.get('page')]).toEqual(['tofix', 'element', 'ОВК', '2']);
    expect(new URLSearchParams(toQuery({ ...base, assignee: 'taken' })).get('scope')).toBe('taken');
  });
});
