import { useState } from 'react';
import { ActionIcon, Button, Group, Menu, Modal, NumberInput, Radio, Stack, Text } from '@mantine/core';
import { IconDots } from '@tabler/icons-react';
import type { SetPairCount } from '../../../api/types';
import { addExclusion, type ClashConfig, cellsForSetPair, type MatrixCell, setCellTolerance } from '../../../lib/clashConfig';

/** Правка черновика из меню с подписью для полосы «есть несохранённые изменения». */
export type LabeledEdit = (change: (config: ClashConfig) => ClashConfig, label: string) => void;

const groupName = (config: ClashConfig, id: string) => config.groups.find((g) => g.id === id)?.name ?? '?';
const cellName = (config: ClashConfig, cell: MatrixCell) => `${groupName(config, cell.groupA)} × ${groupName(config, cell.groupB)}`;

/** Допуск клетки: сначала выбор клетки, если пара наборов попала в несколько, затем новое значение. */
function ToleranceDialog({ config, cells, onApply, onClose }: {
  config: ClashConfig;
  cells: MatrixCell[];
  onApply: (cell: MatrixCell, mm: number) => void;
  onClose: () => void;
}) {
  const [index, setIndex] = useState<string | null>(cells.length === 1 ? '0' : null);
  const cell = index == null ? null : cells[Number(index)];
  const [mm, setMm] = useState<number | ''>(cells.length === 1 ? cells[0].toleranceMm : '');
  return (
    <Modal opened onClose={onClose} title="Допуск клетки матрицы">
      <Stack gap="sm">
        {cells.length > 1 && (
          <Radio.Group label="Пара наборов попала в несколько клеток — какую менять?" value={index}
            onChange={(v) => { setIndex(v); setMm(cells[Number(v)].toleranceMm); }}>
            <Stack gap={4} mt={4}>
              {cells.map((c, i) => <Radio key={i} value={String(i)} label={`«${cellName(config, c)}»: ${c.toleranceMm} мм`} />)}
            </Stack>
          </Radio.Group>
        )}
        {cell && (
          <NumberInput label={`Допуск «${cellName(config, cell)}», мм`} min={0} value={mm} onChange={(v) => setMm(v === '' ? '' : Number(v))} />
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Отмена</Button>
          <Button disabled={!cell || mm === ''} onClick={() => { if (cell && mm !== '') onApply(cell, mm); }}>Применить</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * «⋯» у заголовка группы пары наборов в черновике: превратить шум в правило — не проверять пару, поменять допуск клетки
 * или открыть набор. Правки уходят в несохранённый черновик части.
 */
export function SetPairMenu({ config, pair, onEdit, onOpenSet }: {
  config: ClashConfig;
  pair: SetPairCount;
  onEdit: LabeledEdit;
  onOpenSet: (setId: string) => void;
}) {
  const [tolerance, setTolerance] = useState(false);
  const cells = cellsForSetPair(config, pair.setA, pair.setB);
  const names = `${pair.setAName} × ${pair.setBName}`;
  const sets = [pair.setA, pair.setB].filter((id, i, all) => all.indexOf(id) === i)
    .map((id) => config.sets.find((s) => s.id === id))
    .filter((s) => s != null);

  return (
    <>
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <ActionIcon size="sm" variant="subtle" color="gray" aria-label={`Действия с парой «${names}»`} onClick={(e) => e.stopPropagation()}>
            <IconDots size={14} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Item onClick={() => onEdit((c) => addExclusion(c, pair.setA, pair.setB), `не проверять «${names}»`)}>
            Не проверять эту пару наборов
          </Menu.Item>
          {cells.length > 0 && (
            <Menu.Item onClick={() => setTolerance(true)}>
              {cells.length === 1 ? `Допуск клетки «${cellName(config, cells[0])}»: ${cells[0].toleranceMm} мм…` : 'Допуск клетки…'}
            </Menu.Item>
          )}
          <Menu.Divider />
          {sets.map((s) => (
            <Menu.Item key={s.id} onClick={() => onOpenSet(s.id)}>
              Открыть набор «{s.name}» ({groupName(config, s.groupId)})
            </Menu.Item>
          ))}
          {sets.length === 0 && <Menu.Item disabled><Text size="xs">Наборов пары уже нет в черновике</Text></Menu.Item>}
        </Menu.Dropdown>
      </Menu>
      {tolerance && (
        <ToleranceDialog config={config} cells={cells} onClose={() => setTolerance(false)}
          onApply={(cell, mm) => {
            onEdit((c) => setCellTolerance(c, cell, mm), `допуск «${cellName(config, cell)}» ${mm} мм`);
            setTolerance(false);
          }} />
      )}
    </>
  );
}
