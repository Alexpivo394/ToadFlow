import { useEffect, useState } from 'react';
import { Button, Group, Stack, Table, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import type { ClashPartView } from '../../../api/types';
import { formatDateTime } from '../../../lib/format';
import { ModelPicker, type PickedModel } from '../home/NewPartWizard';

const picksOf = (part: ClashPartView): PickedModel[] =>
  part.models.map((m) => ({ modelId: m.modelId, disciplineOverride: m.disciplineOverride ?? '' }));

/** Модели части по разделам; «Изменить состав» открывает тот же выбор, что в мастере новой части. */
export function ModelsTab({ part, readOnly }: { part: ClashPartView; readOnly: boolean }) {
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [picked, setPicked] = useState<PickedModel[]>(picksOf(part));
  const [saving, setSaving] = useState(false);
  useEffect(() => setPicked(picksOf(part)), [part]);

  async function save() {
    setSaving(true);
    try {
      await apiFetch(`/api/clash/parts/${part.id}/models`, {
        method: 'PUT',
        json: picked.map((p) => ({ modelId: p.modelId, disciplineOverride: p.disciplineOverride.trim() || null })),
      });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
      setEditing(false);
    } finally {
      setSaving(false);
    }
  }

  if (editing)
    return (
      <Stack gap="sm">
        <ModelPicker project={part.object ?? ''} part={part.part} picked={picked} onChange={setPicked} />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => { setPicked(picksOf(part)); setEditing(false); }}>Отмена</Button>
          <Button loading={saving} onClick={save}>Сохранить состав</Button>
        </Group>
      </Stack>
    );

  const byDiscipline = new Map<string, ClashPartView['models']>();
  for (const m of part.models) byDiscipline.set(m.discipline ?? 'Раздел не определён', [...(byDiscipline.get(m.discipline ?? 'Раздел не определён') ?? []), m]);

  return (
    <Stack gap="sm">
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Модель</Table.Th>
            <Table.Th>Раздел</Table.Th>
            <Table.Th>Последняя выгрузка</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {[...byDiscipline].map(([discipline, models]) => [
            <Table.Tr key={`d-${discipline}`} bg="var(--mantine-color-default-hover)">
              <Table.Td colSpan={3}><Text size="xs" fw={700}>{discipline}</Text></Table.Td>
            </Table.Tr>,
            ...models.map((m) => (
              <Table.Tr key={m.modelId}>
                <Table.Td><Text size="sm">{m.name}</Text></Table.Td>
                <Table.Td>{m.discipline ?? '—'}{m.disciplineOverride && <Text span size="xs" c="dimmed"> (вручную)</Text>}</Table.Td>
                <Table.Td>{m.latest ? formatDateTime(m.latest.extractedAt) : <Text span c="orange" size="sm">нет выгрузки</Text>}</Table.Td>
              </Table.Tr>
            )),
          ])}
        </Table.Tbody>
      </Table>
      {!readOnly && <Group><Button size="xs" variant="default" onClick={() => setEditing(true)}>Изменить состав моделей</Button></Group>}
    </Stack>
  );
}
