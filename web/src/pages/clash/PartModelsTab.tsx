import { useEffect, useState } from 'react';
import { Badge, Button, Group, MultiSelect, Stack, Table, Text, TextInput } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import type { ClashModelView, ClashPartView, PartModelView } from '../../api/types';
import { formatDateTime } from '../../lib/format';

/** Модели части и их разделы: раздел — из разбора имени, если не задан вручную. */
export function PartModelsTab({ part, readOnly }: { part: ClashPartView; readOnly: boolean }) {
  const queryClient = useQueryClient();
  const all = useApi<ClashModelView[]>(['clash-models'], '/api/clash/models');
  const [models, setModels] = useState<{ modelId: string; disciplineOverride: string | null }[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => setModels(part.models.map((m) => ({ modelId: m.modelId, disciplineOverride: m.disciplineOverride }))), [part]);

  const byId = new Map(part.models.map((m) => [m.modelId, m]));
  const known = new Map((all.data ?? []).map((m) => [m.id, m]));
  const dirty = JSON.stringify(models) !== JSON.stringify(part.models.map((m) => ({ modelId: m.modelId, disciplineOverride: m.disciplineOverride })));

  async function save(next = models) {
    setSaving(true);
    try {
      await apiFetch(`/api/clash/parts/${part.id}/models`, { method: 'PUT', json: next });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
    } finally {
      setSaving(false);
    }
  }

  async function suggest() {
    const query = new URLSearchParams({ object: part.object ?? '', part: part.part ?? '' });
    const found = await apiFetch<PartModelView[]>(`/api/clash/parts/suggest?${query}`);
    const ids = new Set(models.map((m) => m.modelId));
    await save([...models, ...found.filter((m) => !ids.has(m.modelId)).map((m) => ({ modelId: m.modelId, disciplineOverride: null }))]);
  }

  return (
    <Stack gap="sm">
      {!readOnly && (
        <Group align="flex-end">
          <MultiSelect label="Модели части" w={520} searchable data={(all.data ?? []).map((m) => ({ value: m.id, label: m.name }))}
            value={models.map((m) => m.modelId)}
            onChange={(ids) => setModels(ids.map((id) => models.find((m) => m.modelId === id) ?? { modelId: id, disciplineOverride: null }))} />
          <Button variant="default" onClick={suggest} disabled={!part.object && !part.part}>Добавить модели части</Button>
          <Button onClick={() => save()} disabled={!dirty} loading={saving}>Сохранить модели</Button>
        </Group>
      )}
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Модель</Table.Th>
            <Table.Th>Раздел по имени</Table.Th>
            <Table.Th>Раздел вручную</Table.Th>
            <Table.Th>Последняя выгрузка</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {models.map((m, i) => {
            const view = byId.get(m.modelId);
            const model = known.get(m.modelId);
            const latest = view?.latest ?? model?.latest ?? null;
            return (
              <Table.Tr key={m.modelId}>
                <Table.Td>
                  <Text size="sm">{view?.name ?? model?.name}</Text>
                  <Text size="xs" c="dimmed">{view?.modelPath ?? model?.modelPath}</Text>
                </Table.Td>
                <Table.Td>{view?.parsedDiscipline ?? <Badge color="orange" variant="light">не распознан</Badge>}</Table.Td>
                <Table.Td>
                  <TextInput size="xs" w={120} value={m.disciplineOverride ?? ''} disabled={readOnly} placeholder="как по имени"
                    onChange={(e) => setModels(models.map((x, j) => (j === i ? { ...x, disciplineOverride: e.currentTarget.value || null } : x)))} />
                </Table.Td>
                <Table.Td>{latest ? formatDateTime(latest.extractedAt) : <Text size="sm" c="red">нет — модель не проверится</Text>}</Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}
