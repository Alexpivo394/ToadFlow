import { describe, expect, it } from 'vitest';
import { applyTableState, readTableState, writeTableState, type Column, type TableState } from './tableState';

interface Row {
  name: string;
  role: string;
  last: string | null;
}

const rows: Row[] = [
  { name: 'Борис', role: 'Admin', last: '2026-10-02' },
  { name: 'анна', role: 'Viewer', last: null },
  { name: 'Вера', role: 'Operator', last: '2026-10-05' },
  { name: 'Агата', role: 'Viewer', last: '2026-10-01' },
];

const columns: Column<Row>[] = [
  { key: 'name', title: 'Имя', render: (r) => r.name, sort: (r) => r.name },
  {
    key: 'role', title: 'Роль', render: (r) => r.role, sort: (r) => r.role,
    filter: { options: [{ value: 'Admin', label: 'А' }, { value: 'Viewer', label: 'Н' }, { value: 'Operator', label: 'К' }], match: (r, v) => v.includes(r.role) },
  },
  { key: 'last', title: 'Вход', render: (r) => r.last, sort: (r) => r.last },
];

const state = (patch: Partial<TableState> = {}): TableState => ({ sort: null, dir: 'asc', q: '', filters: {}, page: 1, ...patch });
const names = (list: Row[]) => list.map((r) => r.name);

describe('applyTableState', () => {
  it('sorts with the Russian collator', () => {
    expect(names(applyTableState(rows, columns, state({ sort: 'name' })))).toEqual(['Агата', 'анна', 'Борис', 'Вера']);
    expect(names(applyTableState(rows, columns, state({ sort: 'name', dir: 'desc' })))).toEqual(['Вера', 'Борис', 'анна', 'Агата']);
  });

  it('nulls sort last in both directions', () => {
    expect(names(applyTableState(rows, columns, state({ sort: 'last' })))).toEqual(['Агата', 'Борис', 'Вера', 'анна']);
    expect(names(applyTableState(rows, columns, state({ sort: 'last', dir: 'desc' })))).toEqual(['Вера', 'Борис', 'Агата', 'анна']);
  });

  it('search narrows the rows and is case-insensitive', () => {
    expect(names(applyTableState(rows, columns, state({ q: 'АН' }), (r) => r.name))).toEqual(['анна']);
  });

  it('column filter keeps rows matching any selected value', () => {
    expect(names(applyTableState(rows, columns, state({ filters: { role: ['Admin', 'Operator'] } })))).toEqual(['Борис', 'Вера']);
  });

  it('numbers sort numerically', () => {
    const numbered: Column<{ n: number }>[] = [{ key: 'n', title: 'N', render: (r) => r.n, sort: (r) => r.n }];
    expect(applyTableState([{ n: 10 }, { n: 9 }, { n: 100 }], numbered, state({ sort: 'n' })).map((r) => r.n)).toEqual([9, 10, 100]);
  });
});

describe('table state in the address', () => {
  it('round-trips and drops empty values', () => {
    const params = writeTableState(new URLSearchParams('other=1'), state({ sort: 'name', dir: 'desc', q: 'ив', filters: { role: ['Admin', 'Viewer'], x: [] }, page: 2 }));

    expect(params.get('other')).toBe('1');
    expect(params.get('f.x')).toBeNull();
    expect(readTableState(params)).toEqual(state({ sort: 'name', dir: 'desc', q: 'ив', filters: { role: ['Admin', 'Viewer'] }, page: 2 }));
    expect(writeTableState(params, state()).toString()).toBe('other=1');
  });

  it('a urlKey prefixes the parameters', () => {
    const params = writeTableState(new URLSearchParams(), state({ sort: 'name', q: 'а' }), 'users');

    expect(params.get('users.sort')).toBe('name');
    expect(params.get('sort')).toBeNull();
    expect(readTableState(params)).toEqual(state());
    expect(readTableState(params, 'users').q).toBe('а');
  });
});
