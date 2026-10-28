import { Button, Checkbox, Group, NumberInput, Select, Stack, Table, Text } from '@mantine/core';
import { Empty } from '../../components/ui';
import { matrixCell, setCell, type ClashConfig } from '../../lib/clashConfig';

/** Матрица «раздел × раздел»: проверять ли пару и с каким допуском. Нижний треугольник повторяет верхний. */
export function MatrixEditor({ config, disciplines, readOnly, onChange, onFill }: {
  config: ClashConfig; disciplines: string[]; readOnly: boolean; onChange: (c: ClashConfig) => void; onFill: () => void;
}) {
  if (disciplines.length === 0) return <Empty>Добавьте в часть модели с разделами</Empty>;
  return (
    <Stack gap="sm">
      {!readOnly && <Button variant="default" w="fit-content" onClick={onFill}>Заполнить по умолчанию (все пары, 20 мм)</Button>}
      <Table withColumnBorders>
        <Table.Thead>
          <Table.Tr>
            <Table.Th />
            {disciplines.map((d) => <Table.Th key={d} ta="center">{d}</Table.Th>)}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {disciplines.map((a, i) => (
            <Table.Tr key={a}>
              <Table.Th>{a}</Table.Th>
              {disciplines.map((b, j) => {
                if (j < i) return <Table.Td key={b} bg="var(--mantine-color-default-hover)" />;
                const cell = matrixCell(config, a, b);
                return (
                  <Table.Td key={b}>
                    <Group gap={6} wrap="nowrap" justify="center">
                      <Checkbox aria-label={`Проверять ${a} × ${b}`} checked={cell?.enabled ?? false} disabled={readOnly}
                        onChange={(e) => onChange(setCell(config, a, b, { enabled: e.currentTarget.checked }))} />
                      <NumberInput size="xs" w={78} aria-label={`Допуск ${a} × ${b}`} min={0.05} max={1000} decimalScale={2} suffix=" мм"
                        value={cell?.toleranceMm ?? 20} disabled={readOnly || !(cell?.enabled ?? false)}
                        onChange={(v) => onChange(setCell(config, a, b, { toleranceMm: Number(v) || 0 }))} />
                    </Group>
                  </Table.Td>
                );
              })}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      <Text size="xs" c="dimmed">Коллизия — элементы пересекаются (или один внутри другого) глубже допуска клетки.</Text>
    </Stack>
  );
}

/** Исключения «набор × набор» и соединённые MEP. */
export function ExclusionsEditor({ config, readOnly, onChange }: { config: ClashConfig; readOnly: boolean; onChange: (c: ClashConfig) => void }) {
  const sets = config.sets.map((s) => ({ value: s.id, label: `${s.discipline} · ${s.name}` }));
  return (
    <Stack gap="sm">
      <Checkbox label="Не проверять соединённые элементы MEP друг с другом (труба и её фитинг)" checked={config.ignoreConnectedMep} disabled={readOnly}
        onChange={(e) => onChange({ ...config, ignoreConnectedMep: e.currentTarget.checked })} />
      {config.exclusions.map((e, i) => (
        <Group key={i} gap="xs">
          <Select w={280} aria-label="Набор A" data={sets} value={e.setA} disabled={readOnly} allowDeselect={false}
            onChange={(v) => onChange({ ...config, exclusions: config.exclusions.map((x, j) => (j === i ? { ...x, setA: v! } : x)) })} />
          <Text>×</Text>
          <Select w={280} aria-label="Набор B" data={sets} value={e.setB} disabled={readOnly} allowDeselect={false}
            onChange={(v) => onChange({ ...config, exclusions: config.exclusions.map((x, j) => (j === i ? { ...x, setB: v! } : x)) })} />
          {!readOnly && (
            <Button size="xs" variant="subtle" color="red" onClick={() => onChange({ ...config, exclusions: config.exclusions.filter((_, j) => j !== i) })}>
              Удалить
            </Button>
          )}
        </Group>
      ))}
      {!readOnly && config.sets.length > 0 && (
        <Button variant="default" w="fit-content"
          onClick={() => onChange({ ...config, exclusions: [...config.exclusions, { setA: config.sets[0].id, setB: config.sets[0].id }] })}>
          + Исключение
        </Button>
      )}
    </Stack>
  );
}
