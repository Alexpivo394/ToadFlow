import { Button, Group, Menu, Text } from '@mantine/core';
import type { BulkChoice } from './BulkDialog';

const n = (value: number) => value.toLocaleString('ru-RU');

export const bulkChoices = {
  take: { action: 'Take', label: 'Беру' },
  fake: { action: 'Fake', label: 'Фиктивные' },
  fixed: { action: 'FixedPending', label: 'Исправлены' },
  note: { action: 'Note', label: 'Примечание' },
  seen: { action: 'Seen', label: 'Отметить просмотренными' },
  both: { action: 'Assignee', assignee: 'Both', label: 'Кто правит: обе стороны' },
  sideA: { action: 'Assignee', assignee: 'SideA', label: 'Кто правит: сторона A' },
  sideB: { action: 'Assignee', assignee: 'SideB', label: 'Кто правит: сторона B' },
} satisfies Record<string, BulkChoice>;

/**
 * Полоса выделения: «Выделено N · Выделить все по фильтру (M) · Снять» и действия. «Все по фильтру» есть, когда фильтр
 * шире выделения и сервер может применить действие по фильтру.
 */
export function SelectionBar({ count, filterTotal, byFilter, canEdit, onAllByFilter, onClear, onAction }: {
  count: number;
  /** Сколько коллизий под фильтром; null — «все по фильтру» недоступно. */
  filterTotal: number | null;
  byFilter: boolean;
  canEdit: boolean;
  onAllByFilter: () => void;
  onClear: () => void;
  onAction: (choice: BulkChoice) => void;
}) {
  return (
    <Group gap="xs" px="xs" py={4} wrap="wrap" bg="var(--mantine-primary-color-light)" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      <Text size="xs" fw={600}>Выделено {n(count)}</Text>
      {!byFilter && filterTotal !== null && filterTotal > count && (
        <Button size="compact-xs" variant="subtle" onClick={onAllByFilter}>Выделить все по фильтру ({n(filterTotal)})</Button>
      )}
      <Button size="compact-xs" variant="subtle" color="gray" onClick={onClear}>Снять</Button>
      {canEdit && (
        <>
          <Button size="compact-xs" variant="light" onClick={() => onAction(bulkChoices.take)}>Беру</Button>
          <Menu position="bottom-start" withinPortal>
            <Menu.Target>
              <Button size="compact-xs" variant="light">Кто правит ▾</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item onClick={() => onAction(bulkChoices.both)}>Обе стороны</Menu.Item>
              <Menu.Item onClick={() => onAction(bulkChoices.sideA)}>Сторона A</Menu.Item>
              <Menu.Item onClick={() => onAction(bulkChoices.sideB)}>Сторона B</Menu.Item>
            </Menu.Dropdown>
          </Menu>
          <Button size="compact-xs" variant="light" onClick={() => onAction(bulkChoices.fake)}>Фиктивные…</Button>
          <Button size="compact-xs" variant="light" onClick={() => onAction(bulkChoices.fixed)}>Исправлены</Button>
          <Button size="compact-xs" variant="light" onClick={() => onAction(bulkChoices.note)}>Примечание…</Button>
        </>
      )}
      <Button size="compact-xs" variant="light" color="gray" onClick={() => onAction(bulkChoices.seen)}>Отметить просмотренными</Button>
    </Group>
  );
}
