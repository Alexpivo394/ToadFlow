import { useState } from 'react';
import { Button, Group, NumberInput, Paper, Stack, Table, Text, UnstyledButton } from '@mantine/core';
import { type ClashConfig, matrixCell, setCell } from '../../../lib/clashConfig';

type Change = (change: (config: ClashConfig) => ClashConfig) => void;

const key = (a: string, b: string) => `${a}|${b}`;
const colors = { standard: 'var(--mantine-color-green-light)', custom: 'var(--mantine-color-blue-light)', off: 'var(--mantine-color-default-hover)' };

/**
 * Матрица «группа × группа», только верхний треугольник: «АР × ОВ» и «ОВ × АР» — одна проверка. Цифра — допуск; 20 мм и
 * свой допуск разного цвета. Щелчок — включить или выключить; Shift+щелчок или протянуть — выделить несколько; щелчок по
 * заголовку — выделить строку или столбец.
 */
export function MatrixTab({ config, readOnly, onChange, onFill }: { config: ClashConfig; readOnly: boolean; onChange: Change; onFill: () => void }) {
  const groups = config.groups;
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [dragging, setDragging] = useState(false);
  const [tolerance, setTolerance] = useState<number | string>(20);

  const pairs = (i: number) => groups.slice(i).map((g) => [groups[i].id, g.id] as const);
  const column = (j: number) => groups.slice(0, j + 1).map((g) => [g.id, groups[j].id] as const);
  const select = (cells: (readonly [string, string])[], add: boolean) =>
    setSelected((s) => new Set([...(add ? s : []), ...cells.map(([a, b]) => key(a, b))]));

  const toggle = (a: string, b: string) => {
    const cell = matrixCell(config, a, b);
    onChange((c) => setCell(c, a, b, { enabled: !(cell?.enabled ?? false) }));
  };

  const bulk = (patch: { enabled?: boolean; toleranceMm?: number }) => {
    onChange((c) => [...selected].reduce((acc, k) => {
      const [a, b] = k.split('|');
      return setCell(acc, a, b, patch);
    }, c));
  };

  return (
    <Stack gap="md" onMouseUp={() => setDragging(false)} onMouseLeave={() => setDragging(false)}>
      <Group align="flex-start" gap="xl" wrap="wrap">
        <Table withColumnBorders={false} style={{ width: 'auto', borderCollapse: 'separate', borderSpacing: 2, userSelect: 'none' }}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th />
              {groups.map((g, j) => (
                <Table.Th key={g.id} p={4} style={{ verticalAlign: 'bottom' }}>
                  <UnstyledButton aria-label={`Столбец ${g.name}`} onClick={() => !readOnly && select(column(j), false)}
                    style={{ writingMode: 'vertical-rl', transform: 'rotate(180deg)' }}>
                    <Text size="xs" fw={600}>{g.name}</Text>
                  </UnstyledButton>
                </Table.Th>
              ))}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {groups.map((row, i) => (
              <Table.Tr key={row.id}>
                <Table.Th p={4} ta="right">
                  <UnstyledButton aria-label={`Строка ${row.name}`} onClick={() => !readOnly && select(pairs(i), false)}>
                    <Text size="xs" fw={600}>{row.name}</Text>
                  </UnstyledButton>
                </Table.Th>
                {groups.map((col, j) => {
                  if (j < i) return <Table.Td key={col.id} />;
                  const cell = matrixCell(config, row.id, col.id);
                  const on = cell?.enabled ?? false;
                  const isSelected = selected.has(key(row.id, col.id));
                  const background = !on ? colors.off : cell!.toleranceMm === 20 ? colors.standard : colors.custom;
                  return (
                    <Table.Td key={col.id} p={0}>
                      <UnstyledButton
                        aria-label={`${row.name} × ${col.name}`}
                        disabled={readOnly}
                        w={56} h={32}
                        style={{ background, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: 4,
                          outline: isSelected ? '2px solid var(--mantine-color-yellow-5)' : undefined, outlineOffset: -2 }}
                        onMouseDown={(e) => {
                          if (readOnly) return;
                          if (e.shiftKey) select([[row.id, col.id]], true);
                          else setDragging(true);
                        }}
                        onMouseEnter={() => dragging && select([[row.id, col.id]], true)}
                        onClick={(e) => {
                          if (readOnly || e.shiftKey) return;
                          if (selected.size > 1 && isSelected) return;
                          setSelected(new Set());
                          toggle(row.id, col.id);
                        }}>
                        <Text size="xs" c={on ? undefined : 'dimmed'}>{on ? cell!.toleranceMm : '—'}</Text>
                      </UnstyledButton>
                    </Table.Td>
                  );
                })}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        <Stack gap="xs" maw={340}>
          <Group gap="md">
            <Group gap={4}><div style={{ width: 14, height: 14, background: colors.standard, borderRadius: 2 }} /><Text size="xs">проверяется, 20 мм</Text></Group>
            <Group gap={4}><div style={{ width: 14, height: 14, background: colors.custom, borderRadius: 2 }} /><Text size="xs">свой допуск</Text></Group>
            <Group gap={4}><div style={{ width: 14, height: 14, background: colors.off, borderRadius: 2 }} /><Text size="xs">не проверяется</Text></Group>
          </Group>
          <Text size="xs" c="dimmed">Щелчок — включить или выключить. Shift+щелчок или протянуть мышью — выделить несколько клеток. Щелчок по имени группы — вся строка или столбец.</Text>
          {!readOnly && <Button size="xs" variant="default" w="fit-content" onClick={onFill}>Заполнить по умолчанию (20 мм)</Button>}
        </Stack>
      </Group>
      {!readOnly && selected.size > 0 && (
        <Paper withBorder p="xs" radius="md" style={{ borderColor: 'var(--mantine-color-yellow-5)' }} w="fit-content">
          <Group gap="sm">
            <Text size="sm">Выбрано клеток: {selected.size}</Text>
            <Button size="xs" variant="default" onClick={() => bulk({ enabled: true })}>Включить</Button>
            <Button size="xs" variant="default" onClick={() => bulk({ enabled: false })}>Выключить</Button>
            <NumberInput size="xs" w={90} min={0.05} max={1000} aria-label="Допуск выделенных" value={tolerance} onChange={setTolerance} suffix=" мм" />
            <Button size="xs" onClick={() => bulk({ toleranceMm: Number(tolerance), enabled: true })}>Задать допуск</Button>
            <Button size="xs" variant="subtle" onClick={() => setSelected(new Set())}>Снять выделение</Button>
          </Group>
        </Paper>
      )}
    </Stack>
  );
}
