import type { CSSProperties, ReactNode } from 'react';
import { Checkbox, Group, MultiSelect, Pagination, Stack, Table, TextInput, UnstyledButton } from '@mantine/core';
import { IconArrowDown, IconArrowUp, IconSearch, IconSelector } from '@tabler/icons-react';
import { Empty } from './ui';
import { applyTableState, useTableState, type Column } from './tableState';

export type { Column } from './tableState';

/**
 * Заголовок столбца, сортирующий таблицу щелчком (по возрастанию → по убыванию → как было), — для таблиц со своей вёрсткой
 * (например, со строками, раскрывающими лог); состояние — то же, что у <see cref="DataTable" />.
 */
export function SortHeader({ column, title, urlKey }: { column: string; title: string; urlKey?: string }) {
  const [state, update] = useTableState(urlKey);
  const sortBy = () =>
    update(state.sort !== column ? { sort: column, dir: 'asc' } : state.dir === 'asc' ? { dir: 'desc' } : { sort: null, dir: 'asc' });
  return (
    <UnstyledButton onClick={sortBy} fw={600} fz="sm">
      <Group gap={4} wrap="nowrap">
        {title}
        {state.sort === column ? (state.dir === 'asc' ? <IconArrowUp size={14} /> : <IconArrowDown size={14} />) : <IconSelector size={14} opacity={0.4} />}
      </Group>
    </UnstyledButton>
  );
}

/** Строк на странице, когда таблица делит строки сама. */
const clientPageSize = 50;

/**
 * Общая таблица списков: сортировка щелчком по заголовку (по возрастанию → по убыванию → как было), поиск, фильтры по
 * значениям столбцов, выбор строк для массовых действий. Состояние — в адресе. Небольшие списки сортируются и фильтруются
 * здесь; в режиме <c>server</c> это сделал сервер, а <c>rows</c> — уже нужная страница.
 */
export function DataTable<T>({ rows, columns, rowKey, search, searchPlaceholder, selection, onRowClick, rowStyle, urlKey, server, empty }: {
  rows: T[];
  columns: Column<T>[];
  rowKey: (row: T) => string;
  search?: (row: T) => string;
  searchPlaceholder?: string;
  selection?: { selected: string[]; onChange: (ids: string[]) => void };
  onRowClick?: (row: T) => void;
  rowStyle?: (row: T) => CSSProperties | undefined;
  /** Приставка параметров адреса, если таблиц на странице несколько. */
  urlKey?: string;
  server?: { total: number; pageSize: number };
  empty?: ReactNode;
}) {
  const [state, update] = useTableState(urlKey);
  const filtered = server ? rows : applyTableState(rows, columns, state, search);
  const pageSize = server?.pageSize ?? clientPageSize;
  const total = server?.total ?? filtered.length;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const page = Math.min(state.page, pages);
  const visible = server ? rows : filtered.slice((page - 1) * pageSize, page * pageSize);
  const filters = columns.filter((c) => c.filter);

  const ids = filtered.map(rowKey);
  const chosen = selection ? ids.filter((id) => selection.selected.includes(id)) : [];
  const toggleAll = () => selection!.onChange(chosen.length === ids.length ? [] : ids);
  const toggle = (id: string) =>
    selection!.onChange(selection!.selected.includes(id) ? selection!.selected.filter((x) => x !== id) : [...selection!.selected, id]);

  /** Щелчок по заголовку: по возрастанию → по убыванию → без сортировки. */
  const sortBy = (key: string) =>
    update(state.sort !== key ? { sort: key, dir: 'asc' } : state.dir === 'asc' ? { dir: 'desc' } : { sort: null, dir: 'asc' });

  return (
    <Stack gap="sm">
      {(search || filters.length > 0) && (
        <Group gap="sm" align="flex-end">
          {search && (
            <TextInput w={260} aria-label="Поиск" placeholder={searchPlaceholder ?? 'Поиск…'} leftSection={<IconSearch size={14} />} value={state.q}
              onChange={(e) => update({ q: e.currentTarget.value })} />
          )}
          {filters.map((column) => (
            <MultiSelect key={column.key} w={220} aria-label={`Фильтр: ${column.title}`} placeholder={column.title} clearable data={column.filter!.options}
              value={state.filters[column.key] ?? []} onChange={(values) => update({ filters: { ...state.filters, [column.key]: values } })} />
          ))}
        </Group>
      )}
      {filtered.length === 0 && total === 0 ? (
        <Empty>{empty ?? 'Ничего не найдено'}</Empty>
      ) : (
        <Table.ScrollContainer minWidth={600}>
          <Table highlightOnHover={Boolean(onRowClick)}>
            <Table.Thead>
              <Table.Tr>
                {selection && (
                  <Table.Th w={36}>
                    <Checkbox aria-label="Выбрать все" checked={ids.length > 0 && chosen.length === ids.length}
                      indeterminate={chosen.length > 0 && chosen.length < ids.length} onChange={toggleAll} />
                  </Table.Th>
                )}
                {columns.map((column) => (
                  <Table.Th key={column.key} w={column.width}>
                    {column.sort ? (
                      <UnstyledButton onClick={() => sortBy(column.key)} fw={600} fz="sm">
                        <Group gap={4} wrap="nowrap">
                          {column.title}
                          {state.sort === column.key ? (state.dir === 'asc' ? <IconArrowUp size={14} /> : <IconArrowDown size={14} />)
                            : <IconSelector size={14} opacity={0.4} />}
                        </Group>
                      </UnstyledButton>
                    ) : column.title}
                  </Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {visible.map((row) => {
                const id = rowKey(row);
                return (
                  <Table.Tr key={id} onClick={onRowClick ? () => onRowClick(row) : undefined}
                    style={{ cursor: onRowClick ? 'pointer' : undefined, ...rowStyle?.(row) }}>
                    {selection && (
                      <Table.Td onClick={(e) => e.stopPropagation()}>
                        <Checkbox aria-label="Выбрать" checked={selection.selected.includes(id)} onChange={() => toggle(id)} />
                      </Table.Td>
                    )}
                    {columns.map((column) => <Table.Td key={column.key}>{column.render(row)}</Table.Td>)}
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
      {pages > 1 && <Pagination total={pages} value={page} onChange={(next) => update({ page: next })} />}
    </Stack>
  );
}
