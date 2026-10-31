import { Badge, Group, Stack, Text, UnstyledButton } from '@mantine/core';
import type { ReportRow } from '../../../api/types';
import { CollisionImage } from '../../../components/CollisionImage';
import { Side } from '../approved/ApprovedTab';
import { assigneeLabel, statusLabel, StatusBadge } from '../approved/StatusBadge';

/** Переход в прогоне — короткой меткой: «новая», «вернулась», «исправлена»… */
export function ChangeBadge({ change }: { change: ReportRow['change'] }) {
  if (!change) return null;
  return <Badge size="xs" variant="outline" color={statusLabel[change].color}>в прогоне: {statusLabel[change].label}</Badge>;
}

/** Строки отчёта: картинка 160 × 100, номер, статус и переход, стороны, глубина, уровень, кто правит, последнее примечание. */
export function ReportRows({ rows, selected, onOpen }: { rows: ReportRow[]; selected: string | null; onOpen: (id: string) => void }) {
  return (
    <Stack gap={0}>
      {rows.map((row) => {
        const c = row.collision;
        const level = c.levelA === c.levelB || !c.levelB ? (c.levelA ?? '') : !c.levelA ? c.levelB : `${c.levelA} / ${c.levelB}`;
        return (
          <UnstyledButton key={c.id} onClick={() => onOpen(c.id)} aria-label={`Коллизия ${c.number}`} px="xs" py={6}
            bg={c.id === selected ? 'var(--mantine-primary-color-light)' : undefined}
            style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
            <Group gap="sm" wrap="nowrap" align="flex-start">
              <CollisionImage collisionId={c.id} imageVersion={c.imageVersion} foundNow={c.foundNow} width={160} />
              <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                <Group gap={6}>
                  <Text size="sm" fw={600}>№ {c.number}</Text>
                  <StatusBadge status={c.status} />
                  {row.change && row.change !== c.status && <ChangeBadge change={row.change} />}
                </Group>
                <Side row={c} side="A" />
                <Side row={c} side="B" />
                <Text size="xs" c="dimmed">
                  {Math.round(c.depthMm)} мм{level ? ` · ${level}` : ''} · правит {assigneeLabel[c.assignee]}
                </Text>
                {row.lastNote && (
                  <Text size="xs" truncate>
                    {row.lastNoteBy ? `${row.lastNoteBy}: ` : ''}{row.lastNote}
                  </Text>
                )}
              </Stack>
            </Group>
          </UnstyledButton>
        );
      })}
    </Stack>
  );
}
