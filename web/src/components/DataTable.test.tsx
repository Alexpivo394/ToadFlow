import { fireEvent, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { pickOption, renderUi } from '../test/renderApp';
import { DataTable } from './DataTable';
import type { Column } from './tableState';

interface Row {
  id: string;
  name: string;
  role: string;
}

const rows: Row[] = [
  { id: '1', name: 'Борис', role: 'Admin' },
  { id: '2', name: 'Анна', role: 'Viewer' },
  { id: '3', name: 'Вера', role: 'Viewer' },
];

const columns: Column<Row>[] = [
  { key: 'name', title: 'Имя', render: (r) => r.name, sort: (r) => r.name },
  {
    key: 'role', title: 'Роль', render: (r) => r.role, sort: (r) => r.role,
    filter: { options: [{ value: 'Admin', label: 'Администратор' }, { value: 'Viewer', label: 'Наблюдатель' }], match: (r, v) => v.includes(r.role) },
  },
];

function Where() {
  const location = useLocation();
  return <div data-testid="where">{decodeURIComponent(location.search)}</div>;
}

const bodyNames = (table?: HTMLElement) =>
  within(table ?? screen.getAllByRole('table')[0]).getAllByRole('row').slice(1).map((row) => within(row).getAllByRole('cell')[row.querySelector('input[type=checkbox]') ? 1 : 0].textContent);

describe('DataTable', () => {
  it('clicking a header sorts ascending, again descending, third time resets', () => {
    renderUi(<><DataTable rows={rows} columns={columns} rowKey={(r) => r.id} /><Where /></>, {});
    const header = screen.getByRole('button', { name: /Имя/ });

    fireEvent.click(header);
    expect(bodyNames()).toEqual(['Анна', 'Борис', 'Вера']);
    expect(screen.getByTestId('where').textContent).toBe('?sort=name');
    fireEvent.click(header);
    expect(bodyNames()).toEqual(['Вера', 'Борис', 'Анна']);
    fireEvent.click(header);
    expect(bodyNames()).toEqual(['Борис', 'Анна', 'Вера']);
    expect(screen.getByTestId('where').textContent).toBe('');
  });

  it('state is written to the query string and restored from it', async () => {
    renderUi(<DataTable rows={rows} columns={columns} rowKey={(r) => r.id} search={(r) => r.name} />, {}, '/?sort=name&dir=desc&q=а');

    expect(bodyNames()).toEqual(['Вера', 'Анна']);
    expect((screen.getByRole('textbox', { name: 'Поиск' }) as HTMLInputElement).value).toBe('а');
  });

  it('column filter narrows rows', async () => {
    renderUi(<><DataTable rows={rows} columns={columns} rowKey={(r) => r.id} /><Where /></>, {});

    await pickOption(screen.getAllByLabelText('Фильтр: Роль').find((e) => e.tagName === 'INPUT')!, 'Наблюдатель');

    expect(bodyNames()).toEqual(['Анна', 'Вера']);
    expect(screen.getByTestId('where').textContent).toBe('?f.role=Viewer');
  });

  it('two tables with different urlKey do not share state', () => {
    renderUi(
      <>
        <DataTable rows={rows} columns={columns} rowKey={(r) => r.id} urlKey="a" />
        <DataTable rows={rows} columns={columns} rowKey={(r) => r.id} urlKey="b" />
        <Where />
      </>,
      {},
    );

    fireEvent.click(screen.getAllByRole('button', { name: /Имя/ })[0]);

    const [first, second] = screen.getAllByRole('table');
    expect(bodyNames(first)).toEqual(['Анна', 'Борис', 'Вера']);
    expect(bodyNames(second)).toEqual(['Борис', 'Анна', 'Вера']);
    expect(screen.getByTestId('where').textContent).toBe('?a.sort=name');
  });

  it('select-all selects only the filtered rows', async () => {
    function Selectable() {
      const [selected, setSelected] = useState<string[]>([]);
      return (
        <>
          <DataTable rows={rows} columns={columns} rowKey={(r) => r.id} search={(r) => r.name} selection={{ selected, onChange: setSelected }} />
          <div data-testid="selected">{selected.join(',')}</div>
        </>
      );
    }
    renderUi(<Selectable />, {}, '/?f.role=Viewer');

    fireEvent.click(screen.getByRole('checkbox', { name: 'Выбрать все' }));

    expect(screen.getByTestId('selected').textContent).toBe('2,3');
  });

  it('server mode does not re-sort rows and shows pagination from total', () => {
    renderUi(<DataTable rows={rows} columns={columns} rowKey={(r) => r.id} server={{ total: 120, pageSize: 50 }} />, {}, '/?sort=name');

    expect(bodyNames()).toEqual(['Борис', 'Анна', 'Вера']);
    expect(screen.getByRole('button', { name: '3' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '4' })).toBeNull();
  });

  it('shows the empty state', () => {
    renderUi(<DataTable rows={[]} columns={columns} rowKey={(r: Row) => r.id} empty="Никого нет" />, {});

    expect(screen.getByText('Никого нет')).toBeTruthy();
  });
});
