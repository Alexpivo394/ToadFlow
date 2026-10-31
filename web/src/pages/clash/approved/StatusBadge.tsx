import { Badge } from '@mantine/core';
import type { CollisionAssignee, CollisionStatus } from '../../../api/types';

export const statusLabel: Record<CollisionStatus, { label: string; color: string }> = {
  New: { label: 'новая', color: 'red' },
  Active: { label: 'активная', color: 'orange' },
  Returned: { label: 'вернулась', color: 'grape' },
  FixedPending: { label: 'исправлена (ждёт)', color: 'teal' },
  Fake: { label: 'фиктивная', color: 'gray' },
  Fixed: { label: 'исправлена', color: 'green' },
  GoneByRules: { label: 'ушла из-за правил', color: 'gray' },
  Unchecked: { label: 'не проверена', color: 'yellow' },
};

/** Открытые статусы — фильтр по умолчанию. */
export const openStatuses: CollisionStatus[] = ['New', 'Active', 'Returned', 'FixedPending', 'Unchecked'];

export const assigneeLabel: Record<CollisionAssignee, string> = { Both: 'обе стороны', SideA: 'сторона A', SideB: 'сторона B' };

export function StatusBadge({ status }: { status: CollisionStatus }) {
  return <Badge size="xs" variant="light" color={statusLabel[status].color}>{statusLabel[status].label}</Badge>;
}
