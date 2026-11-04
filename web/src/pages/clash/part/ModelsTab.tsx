import { useEffect, useState } from 'react';
import { Alert, Button, Group, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import type { ClashPartView } from '../../../api/types';
import { askConfirm } from '../../../components/ui';
import { formatDateTime } from '../../../lib/format';
import { ModelPicker, type PickedModel } from '../home/NewPartWizard';

const picksOf = (part: ClashPartView): PickedModel[] =>
  part.models.map((m) => ({ modelId: m.modelId, disciplineOverride: m.disciplineOverride ?? '' }));

/**
 * Модели части по разделам. «Убрать» у строки убирает одну модель; «Изменить состав» открывает тот же выбор, что в мастере
 * новой части. Если в части не осталось моделей раздела, сервер убирает из черновика группу раздела с её наборами.
 */
export function ModelsTab({ part, readOnly }: { part: ClashPartView; readOnly: boolean }) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [picked, setPicked] = useState<PickedModel[]>(picksOf(part));
  const [saving, setSaving] = useState(false);
  useEffect(() => setPicked(picksOf(part)), [part]);

  async function save(models: PickedModel[]) {
    setSaving(true);
    try {
      await apiFetch(`/api/clash/parts/${part.id}/models`, {
        method: 'PUT',
        json: models.map((p) => ({ modelId: p.modelId, disciplineOverride: p.disciplineOverride.trim() || null })),
      });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
      setEditing(false);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Состав не сохранён', message: (error as Error).message });
    } finally {
      setSaving(false);
    }
  }

  function remove(model: ClashPartView['models'][number]) {
    const alone = model.discipline != null && !part.models.some((m) => m.modelId !== model.modelId && m.discipline === model.discipline);
    askConfirm(
      alone
        ? `Убрать «${model.name}» из части? Других моделей раздела ${model.discipline} в части нет, поэтому из черновика уйдут группа ${model.discipline}, её наборы и клетки матрицы.`
        : `Убрать «${model.name}» из части?`,
      () => void save(picksOf(part).filter((p) => p.modelId !== model.modelId)),
    );
  }

  if (editing)
    return (
      <Stack gap="sm">
        <ModelPicker project={part.object ?? ''} part={part.part} picked={picked} onChange={setPicked} />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => { setPicked(picksOf(part)); setEditing(false); }}>Отмена</Button>
          <Button loading={saving} onClick={() => void save(picked)}>Сохранить состав</Button>
        </Group>
      </Stack>
    );

  const byDiscipline = new Map<string, ClashPartView['models']>();
  for (const m of part.models) byDiscipline.set(m.discipline ?? 'Раздел не определён', [...(byDiscipline.get(m.discipline ?? 'Раздел не определён') ?? []), m]);
  const columns = readOnly ? 3 : 4;

  return (
    <Stack gap="sm">
      {part.compositionPending && (
        <Alert color="yellow" variant="light" p="xs">
          <Text size="sm">Изменения состава войдут в проверку после утверждения</Text>
        </Alert>
      )}
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Модель</Table.Th>
            <Table.Th>Раздел</Table.Th>
            <Table.Th>Последняя выгрузка</Table.Th>
            {!readOnly && <Table.Th />}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {[...byDiscipline].map(([discipline, models]) => [
            <Table.Tr key={`d-${discipline}`} bg="var(--mantine-color-default-hover)">
              <Table.Td colSpan={columns}><Text size="xs" fw={700}>{discipline}</Text></Table.Td>
            </Table.Tr>,
            ...models.map((m) => (
              <Table.Tr key={m.modelId}>
                <Table.Td><Text size="sm">{m.name}</Text></Table.Td>
                <Table.Td>{m.discipline ?? '—'}{m.disciplineOverride && <Text span size="xs" c="dimmed"> (вручную)</Text>}</Table.Td>
                <Table.Td>{m.latest ? formatDateTime(m.latest.extractedAt) : <Text span c="orange" size="sm">нет выгрузки</Text>}</Table.Td>
                {!readOnly && (
                  <Table.Td ta="right">
                    <Button size="compact-xs" variant="subtle" color="red" disabled={saving} onClick={() => remove(m)}>Убрать из части</Button>
                  </Table.Td>
                )}
              </Table.Tr>
            )),
          ])}
        </Table.Tbody>
      </Table>
      {!readOnly && <Group><Button size="xs" variant="default" onClick={() => setEditing(true)}>Изменить состав моделей</Button></Group>}
    </Stack>
  );
}
