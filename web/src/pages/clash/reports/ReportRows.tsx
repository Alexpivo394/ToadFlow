import { Fragment } from 'react';
import { Badge, Group, Stack, Text, UnstyledButton } from '@mantine/core';
import type { CollisionRow, ReportLevelView, ReportPairView, ReportRow } from '../../../api/types';
import { CollisionImage } from '../../../components/CollisionImage';
import { categoryLabel } from '../../../lib/categories';
import { assigneeLabel, statusLabel, StatusBadge } from '../approved/StatusBadge';

/** Подпись строки без уровня — в заголовке и в фильтре. */
export const NoLevel = 'Без уровня';

/** Переход в прогоне — короткой меткой: «новая», «вернулась», «исправлена»… */
export function ChangeBadge({ change }: { change: ReportRow['change'] }) {
  if (!change) return null;
  return <Badge size="xs" variant="outline" color={statusLabel[change].color}>в прогоне: {statusLabel[change].label}</Badge>;
}

/** Ключ пары строки — как на сервере: разделы по алфавиту. */
export function pairKey(c: CollisionRow): string {
  const a = c.disciplineA ?? '';
  const b = c.disciplineB ?? '';
  return a <= b ? `${a} × ${b}` : `${b} × ${a}`;
}

const groupKey = (pair: string, level: string) => `${pair}\u001f${level}`;

/** Сторона одной строкой: модель · категория · семейство · тип · ID. */
function ReportSide({ row, side }: { row: CollisionRow; side: 'A' | 'B' }) {
  const a = side === 'A';
  const category = a ? row.categoryA : row.categoryB;
  const parts = [
    a ? row.modelNameA : row.modelNameB,
    category ? categoryLabel(category) : null,
    a ? row.familyA : row.familyB,
    a ? row.typeA : row.typeB,
    `ID ${a ? row.elementIdA : row.elementIdB}`,
  ].filter(Boolean);
  return <Text size="xs" c={a ? 'red.7' : 'blue.7'} truncate>{parts.join(' · ')}</Text>;
}

function Row({ row, selected, onOpen }: { row: ReportRow; selected: boolean; onOpen: (id: string) => void }) {
  const c = row.collision;
  return (
    <UnstyledButton onClick={() => onOpen(c.id)} aria-label={`Коллизия ${c.number}`} px="xs" py={4}
      bg={selected ? 'var(--mantine-primary-color-light)' : undefined}
      style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      <Group gap="sm" wrap="nowrap" align="flex-start">
        <CollisionImage collisionId={c.id} imageVersion={c.imageVersion} width={120} />
        <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
          <Group gap={6}>
            <Text size="sm" fw={600}>№ {c.number}</Text>
            <StatusBadge status={c.status} />
            {row.change && row.change !== c.status && <ChangeBadge change={row.change} />}
            <Text size="xs" c="dimmed">{Math.round(c.depthMm)} мм · правит {assigneeLabel[c.assignee]}</Text>
          </Group>
          <ReportSide row={c} side="A" />
          <ReportSide row={c} side="B" />
          {row.lastNote && <Text size="xs" truncate>{row.lastNoteBy ? `${row.lastNoteBy}: ` : ''}{row.lastNote}</Text>}
        </Stack>
      </Group>
    </UnstyledButton>
  );
}

/**
 * Строки отчёта с заголовками: в разделе — пары (подписи в порядке справочника), при сортировке по уровням — уровни с числом
 * коллизий во всей выборке. Группа, начатая на прошлой странице, помечена «(продолжение)»: начало группы считается по
 * упорядоченному списку уровней выборки (`levels`) и сдвигу страницы (`offset`).
 */
export function ReportRows({ rows, pairs, levels, offset, showPairs, showLevels, selected, onOpen }: {
  rows: ReportRow[];
  pairs: ReportPairView[];
  levels: ReportLevelView[] | undefined;
  offset: number;
  showPairs: boolean;
  showLevels: boolean;
  selected: string | null;
  onOpen: (id: string) => void;
}) {
  const labels = new Map(pairs.map((p) => [p.pair, p.label]));
  const groups = new Map<string, { start: number; count: number }>();
  const pairStarts = new Map<string, number>();
  let start = 0;
  for (const l of levels ?? []) {
    groups.set(groupKey(l.pair, l.level), { start, count: l.count });
    if (!pairStarts.has(l.pair)) pairStarts.set(l.pair, start);
    start += l.count;
  }
  const continued = (groupStart: number | undefined) => (groupStart !== undefined && groupStart < offset ? ' (продолжение)' : '');

  let lastPair: string | null = null;
  let lastGroup: string | null = null;
  return (
    <Stack gap={0}>
      {rows.map((row) => {
        const pair = pairKey(row.collision);
        const key = groupKey(pair, row.level);
        const pairHead = showPairs && pair !== lastPair;
        const levelHead = showLevels && levels !== undefined && key !== lastGroup;
        lastPair = pair;
        lastGroup = key;
        const group = groups.get(key);
        return (
          <Fragment key={row.collision.id}>
            {pairHead && (
              <Text data-testid="pair-header" fw={700} size="sm" mt="sm" px="xs">
                {labels.get(pair) ?? pair}{continued(pairStarts.get(pair))}
              </Text>
            )}
            {levelHead && (
              <Text data-testid="level-header" fw={600} size="xs" c="dimmed" mt={6} px="xs" py={2} bg="var(--mantine-color-default-hover)">
                {row.level || NoLevel} · {(group?.count ?? 0).toLocaleString('ru-RU')}{continued(group?.start)}
              </Text>
            )}
            <Row row={row} selected={row.collision.id === selected} onOpen={onOpen} />
          </Fragment>
        );
      })}
    </Stack>
  );
}
