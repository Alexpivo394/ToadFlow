import { Fragment, useState } from 'react';
import { Table, Text } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ClashSnapshotView, ProjectModelView } from '../../../api/types';
import { Empty, YearBadge } from '../../../components/ui';
import { formatBytes, formatDateTime } from '../../../lib/format';
import type { ProjectKey } from './ProjectList';

function History({ modelId }: { modelId: string }) {
  const snapshots = useApi<ClashSnapshotView[]>(['clash-models', modelId, 'snapshots'], `/api/clash/models/${modelId}/snapshots`);
  return (
    <>
      {(snapshots.data ?? []).map((s) => (
        <Table.Tr key={s.id} bg="var(--mantine-color-default-hover)">
          <Table.Td pl="xl" colSpan={3}><Text size="xs" c="dimmed">выгрузка · {s.viewName} <YearBadge year={s.revitYear} muted /></Text></Table.Td>
          <Table.Td>{formatDateTime(s.extractedAt)}</Table.Td>
          <Table.Td ta="right">{s.elements.toLocaleString('ru-RU')}</Table.Td>
          <Table.Td ta="right">{formatBytes(s.geometryBytes + s.parametersBytes)}</Table.Td>
          <Table.Td />
        </Table.Tr>
      ))}
    </>
  );
}

/** Выгрузки моделей проекта: часть, раздел, дата последней выгрузки (старые подсвечены), история по щелчку. */
export function UploadsTab({ project }: { project: ProjectKey }) {
  const models = useApi<ProjectModelView[]>(['clash-projects', 'models', project], `/api/clash/projects/models?object=${encodeURIComponent(project)}`);
  const [open, setOpen] = useState<string | null>(null);
  if (models.data && models.data.length === 0) return <Empty>Моделей проекта ещё нет — создайте расписание «Выгрузка геометрии для коллизий»</Empty>;

  return (
    <Table highlightOnHover style={{ flex: 1 }}>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Модель</Table.Th>
          <Table.Th>Часть</Table.Th>
          <Table.Th>Раздел</Table.Th>
          <Table.Th>Последняя выгрузка</Table.Th>
          <Table.Th ta="right">Элементов</Table.Th>
          <Table.Th ta="right">Размер</Table.Th>
          <Table.Th ta="right">Хранится</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {(models.data ?? []).map((m) => (
          <Fragment key={m.id}>
            <Table.Tr style={{ cursor: m.latest ? 'pointer' : undefined }} onClick={() => m.latest && setOpen(open === m.id ? null : m.id)}>
              <Table.Td><Text size="sm" fw={500}>{m.name}</Text></Table.Td>
              <Table.Td>{m.part ?? '—'}</Table.Td>
              <Table.Td>{m.discipline ?? '—'}</Table.Td>
              <Table.Td>
                {m.latest
                  ? <Text size="sm" c={m.stale ? 'orange' : undefined}>{m.stale ? 'старая · ' : ''}{formatDateTime(m.latest.extractedAt)}</Text>
                  : <Text size="sm" c="orange">нет выгрузки</Text>}
              </Table.Td>
              <Table.Td ta="right">{m.latest ? m.latest.elements.toLocaleString('ru-RU') : '—'}</Table.Td>
              <Table.Td ta="right">{m.latest ? formatBytes(m.latest.geometryBytes + m.latest.parametersBytes) : '—'}</Table.Td>
              <Table.Td ta="right">{m.snapshotCount}</Table.Td>
            </Table.Tr>
            {open === m.id && <History modelId={m.id} />}
          </Fragment>
        ))}
      </Table.Tbody>
    </Table>
  );
}
