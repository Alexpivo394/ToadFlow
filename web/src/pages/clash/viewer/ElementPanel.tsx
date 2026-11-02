import { useState } from 'react';
import { Badge, Group, Loader, ScrollArea, Stack, Table, Text, TextInput } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ViewerElementProperties, ViewerElementView, ViewerSceneView } from '../../../api/types';
import { roleColor } from '../../../lib/viewer';

const roleName = { A: 'Элемент A', B: 'Элемент B', set: 'Элемент набора', context: 'Окружение' } as const;

function Brief({ element }: { element: ViewerElementView }) {
  return (
    <Stack gap={2}>
      <Group gap="xs">
        <Badge color={roleColor(element.role).color} variant="filled" size="sm">{roleName[element.role]}</Badge>
        <Text size="xs" c="dimmed">{element.discipline}</Text>
      </Group>
      <Text size="sm">{element.category} · {element.family ?? '—'} · {element.type ?? '—'}</Text>
      <Text size="xs" c="dimmed">{element.modelName} · ID {element.elementId} · {element.level ?? 'без уровня'}</Text>
    </Stack>
  );
}

function Properties({ runId, collisionId, element }: { runId: string; collisionId?: string; element: ViewerElementView }) {
  const [filter, setFilter] = useState('');
  // Коллизия утверждённой проверки — свой маршрут: он открыт и проектировщику, маршрут прогона — нет.
  const tail = `/elements/${element.modelId}/${encodeURIComponent(element.uniqueId)}`;
  const path = collisionId ? `/api/clash/collisions/${collisionId}${tail}` : `/api/clash/runs/${runId}${tail}`;
  const properties = useApi<ViewerElementProperties>(['clash-viewer', runId, 'element', element.modelId, element.uniqueId], path);
  if (properties.error) return <Text size="sm" c="red">{properties.error.message}</Text>;
  if (!properties.data) return <Loader size="sm" />;

  const p = properties.data;
  const wanted = filter.trim().toLowerCase();
  const rows = p.parameters.filter((x) => !wanted || x.name.toLowerCase().includes(wanted) || (x.text ?? '').toLowerCase().includes(wanted));
  return (
    <Stack gap="xs">
      <Text size="sm" fw={600}>{p.family ?? '—'} · {p.type ?? '—'}</Text>
      <Table withRowBorders={false} verticalSpacing={2} fz="xs">
        <Table.Tbody>
          {[
            ['Категория', p.category],
            ['Модель', `${p.modelName} (${p.discipline})`],
            ['Уровень', p.level ?? '—'],
            ['Рабочий набор', p.workset ?? '—'],
            ['ElementId', String(p.elementId)],
            ['UniqueId', p.uniqueId],
          ].map(([name, value]) => (
            <Table.Tr key={name}>
              <Table.Td c="dimmed" w={110}>{name}</Table.Td>
              <Table.Td style={{ wordBreak: 'break-all' }}>{value}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      <TextInput size="xs" placeholder="Поиск параметра" value={filter} onChange={(e) => setFilter(e.currentTarget.value)} />
      <Table striped verticalSpacing={2} fz="xs">
        <Table.Tbody>
          {rows.map((x, i) => (
            <Table.Tr key={`${x.name}-${i}`}>
              <Table.Td>{x.name}</Table.Td>
              <Table.Td>{x.text ?? (x.number != null ? String(x.number) : '—')}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/** Правая панель просмотрщика: кратко A и B, по щелчку в сцене — все свойства выбранного элемента. */
export function ElementPanel({ runId, collisionId, scene, selected }: {
  runId: string;
  collisionId?: string;
  scene: ViewerSceneView | undefined;
  selected: number | null;
}) {
  const element = selected != null ? scene?.elements[selected] : undefined;
  return (
    <ScrollArea h="100%" type="auto">
      <Stack gap="md" pr="sm">
        {scene?.elements.filter((e) => e.role !== 'context').map((e) => <Brief key={e.index} element={e} />)}
        {element ? (
          <Stack gap="xs">
            <Brief element={element} />
            <Properties runId={runId} collisionId={collisionId} element={element} />
          </Stack>
        ) : (
          <Text size="xs" c="dimmed">Щёлкните элемент в сцене, чтобы увидеть его свойства</Text>
        )}
      </Stack>
    </ScrollArea>
  );
}
