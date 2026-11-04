import { useState } from 'react';
import { Button, Group, NumberInput, SegmentedControl, Stack, Table, Text, UnstyledButton } from '@mantine/core';
import { type ClashConfig, matrixCell, setCell } from '../../../lib/clashConfig';

type Change = (change: (config: ClashConfig) => ClashConfig) => void;

/** Кисть: допуск в мм или «не проверять». */
type Brush = number | 'off';

const colors = { standard: 'var(--mantine-color-green-light)', custom: 'var(--mantine-color-blue-light)', off: 'var(--mantine-color-default-hover)' };
const presets = [20, 50, 100];

/**
 * Матрица «группа × группа», только верхний треугольник: «АР × ОВ» и «ОВ × АР» — одна проверка. Цифра — допуск; 20 мм и
 * свой допуск разного цвета. Правка — кистью: выбрать допуск (или «не проверять») и щёлкнуть или протянуть мышью по клеткам;
 * щелчок по клетке, уже такой же, как кисть, выключает её; щелчок по имени группы красит строку или столбец.
 */
export function MatrixTab({ config, readOnly, onChange }: { config: ClashConfig; readOnly: boolean; onChange: Change }) {
  const groups = config.groups;
  const [brush, setBrush] = useState<Brush>(20);
  const [custom, setCustom] = useState<number | string>('');
  // Что делает протягивание: красит кистью или выключает — решает первая клетка.
  const [stroke, setStroke] = useState<'paint' | 'off' | null>(null);

  const row = (i: number) => groups.slice(i).map((g) => [groups[i].id, g.id] as const);
  const column = (j: number) => groups.slice(0, j + 1).map((g) => [g.id, groups[j].id] as const);
  const all = () => groups.flatMap((_, i) => row(i));

  const apply = (cells: (readonly [string, string])[], mode: 'paint' | 'off') =>
    onChange((c) => cells.reduce((acc, [a, b]) => setCell(acc, a, b,
      mode === 'off' || brush === 'off' ? { enabled: false } : { enabled: true, toleranceMm: brush }), c));

  const sameAsBrush = (a: string, b: string) => {
    const cell = matrixCell(config, a, b);
    return brush !== 'off' && (cell?.enabled ?? false) && cell!.toleranceMm === brush;
  };

  const start = (a: string, b: string) => {
    const mode = sameAsBrush(a, b) ? 'off' : 'paint';
    setStroke(mode);
    apply([[a, b]], mode);
  };

  const pickCustom = (value: number | string) => {
    setCustom(value);
    const mm = Number(value);
    if (value !== '' && mm >= 0.05 && mm <= 1000) setBrush(mm);
  };

  const brushValue = brush === 'off' ? 'off' : presets.includes(brush) ? String(brush) : 'custom';
  // Легенда — по допускам, которые в матрице есть на самом деле, а не «20 мм» вообще (W-7).
  const tolerances = [...new Set(all().map(([a, b]) => matrixCell(config, a, b)).filter((c) => c?.enabled).map((c) => c!.toleranceMm))]
    .sort((x, y) => x - y);

  return (
    <Stack gap="md" onMouseUp={() => setStroke(null)} onMouseLeave={() => setStroke(null)}>
      {!readOnly && (
        <Group gap="sm" wrap="wrap">
          <Text size="sm" fw={500}>Кисть:</Text>
          <SegmentedControl size="xs" value={brushValue}
            onChange={(v) => (v === 'off' ? setBrush('off') : v === 'custom' ? pickCustom(custom === '' ? 30 : custom) : setBrush(Number(v)))}
            data={[{ value: 'off', label: 'Не проверять' }, ...presets.map((p) => ({ value: String(p), label: `${p} мм` })),
              { value: 'custom', label: 'Свой' }]} />
          <NumberInput size="xs" w={100} min={0.05} max={1000} decimalScale={2} aria-label="Свой допуск" placeholder="свой, мм"
            value={custom} onChange={pickCustom} suffix=" мм" />
          <Button size="xs" variant="default" onClick={() => onChange((c) =>
            all().reduce((acc, [a, b]) => setCell(acc, a, b, { enabled: true, toleranceMm: 20 }), c))}>Все клетки — 20 мм</Button>
        </Group>
      )}
      <Group align="flex-start" gap="xl" wrap="wrap">
        <Table withColumnBorders={false} style={{ width: 'auto', borderCollapse: 'separate', borderSpacing: 2, userSelect: 'none' }}>
          <Table.Thead>
            <Table.Tr>
              <Table.Th />
              {groups.map((g, j) => (
                <Table.Th key={g.id} p={4} style={{ verticalAlign: 'bottom' }}>
                  <UnstyledButton aria-label={`Столбец ${g.name}`} onClick={() => !readOnly && apply(column(j), 'paint')}
                    style={{ writingMode: 'vertical-rl', transform: 'rotate(180deg)' }}>
                    <Text size="xs" fw={600}>{g.name}</Text>
                  </UnstyledButton>
                </Table.Th>
              ))}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {groups.map((r, i) => (
              <Table.Tr key={r.id}>
                <Table.Th p={4} ta="right">
                  <UnstyledButton aria-label={`Строка ${r.name}`} onClick={() => !readOnly && apply(row(i), 'paint')}>
                    <Text size="xs" fw={600}>{r.name}</Text>
                  </UnstyledButton>
                </Table.Th>
                {groups.map((col, j) => {
                  if (j < i) return <Table.Td key={col.id} />;
                  const cell = matrixCell(config, r.id, col.id);
                  const on = cell?.enabled ?? false;
                  const background = !on ? colors.off : cell!.toleranceMm === 20 ? colors.standard : colors.custom;
                  return (
                    <Table.Td key={col.id} p={0}>
                      <UnstyledButton
                        aria-label={`${r.name} × ${col.name}`}
                        disabled={readOnly}
                        w={56} h={32}
                        style={{ background, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: 4,
                          cursor: readOnly ? undefined : 'crosshair' }}
                        onMouseDown={(e) => {
                          if (readOnly || e.button !== 0) return;
                          e.preventDefault();
                          start(r.id, col.id);
                        }}
                        onMouseEnter={() => stroke && apply([[r.id, col.id]], stroke)}
                        onKeyDown={(e) => {
                          if (readOnly || (e.key !== 'Enter' && e.key !== ' ')) return;
                          e.preventDefault();
                          start(r.id, col.id);
                          setStroke(null);
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
            <Group gap={4}><div style={{ width: 14, height: 14, background: colors.standard, borderRadius: 2 }} /><Text size="xs">20 мм</Text></Group>
            <Group gap={4}><div style={{ width: 14, height: 14, background: colors.custom, borderRadius: 2 }} /><Text size="xs">другой допуск</Text></Group>
            <Group gap={4}><div style={{ width: 14, height: 14, background: colors.off, borderRadius: 2 }} /><Text size="xs">не проверяется</Text></Group>
          </Group>
          <Text size="xs" c="dimmed">
            {tolerances.length > 0 ? `Проверяется, допуск по клетке (цифра в клетке). Допуски в матрице: ${tolerances.map((t) => `${t.toLocaleString('ru-RU')} мм`).join(', ')}` : 'Ни одна клетка не проверяется'}
          </Text>
          {!readOnly && (
            <Text size="xs" c="dimmed">
              Выберите кисть и щёлкните по клетке или протяните мышью по нескольким. Щелчок по клетке с тем же допуском выключает её.
              Щелчок по имени группы — вся строка или столбец. Изменения сохраняются кнопкой «Сохранить».
            </Text>
          )}
        </Stack>
      </Group>
    </Stack>
  );
}
