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

/**
 * «Кто правит» для человека — разделами пары: АР × ОВ — «АР», «ОВ», «АР и ОВ» (как лента на сервере). Коллизию внутри
 * раздела разделом не различить — «сторона A / B», «обе стороны».
 */
export function assigneeText(assignee: CollisionAssignee, c: { disciplineA?: string | null; disciplineB?: string | null }): string {
  const a = c.disciplineA?.trim() ?? '';
  const b = c.disciplineB?.trim() ?? '';
  const named = a !== '' && b !== '' && a.toLowerCase() !== b.toLowerCase();
  if (assignee === 'SideA') return named ? a : 'сторона A';
  if (assignee === 'SideB') return named ? b : 'сторона B';
  return named ? `${a} и ${b}` : 'обе стороны';
}

export function StatusBadge({ status }: { status: CollisionStatus }) {
  return <Badge size="xs" variant="light" color={statusLabel[status].color}>{statusLabel[status].label}</Badge>;
}
