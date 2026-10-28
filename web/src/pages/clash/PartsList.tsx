import { useState } from 'react';
import { useNavigate } from 'react-router';
import { Button, Group, Modal, Stack, Table, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { ClashPartSummary, ClashPartView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Empty, Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';

/** Части проекта: модели, проверяемые вместе, и черновик их проверки. */
export function PartsList() {
  const { can } = useAuth();
  const navigate = useNavigate();
  const parts = useApi<ClashPartSummary[]>(['clash-parts'], '/api/clash/parts');
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState({ name: '', object: '', part: '' });
  const create = useApiMutation<typeof form, ClashPartView>('POST', () => '/api/clash/parts', { invalidate: ['clash-parts'] });

  return (
    <Panel title="Части проекта" actions={can('Operator') && <Button size="xs" onClick={() => setCreating(true)}>+ Часть</Button>}>
      {(parts.data ?? []).length === 0 ? (
        <Empty>Частей ещё нет. Часть — это модели, которые проверяются вместе: объект и часть здания.</Empty>
      ) : (
        <Table highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Часть</Table.Th>
              <Table.Th>Объект</Table.Th>
              <Table.Th>Часть здания</Table.Th>
              <Table.Th ta="right">Моделей</Table.Th>
              <Table.Th ta="right">Наборов</Table.Th>
              <Table.Th>Изменена</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {parts.data!.map((p) => (
              <Table.Tr key={p.id} style={{ cursor: 'pointer' }} onClick={() => navigate(`/clash/parts/${p.id}`)}>
                <Table.Td><Text size="sm" fw={500}>{p.name}</Text></Table.Td>
                <Table.Td>{p.object ?? '—'}</Table.Td>
                <Table.Td>{p.part ?? '—'}</Table.Td>
                <Table.Td ta="right">{p.models}</Table.Td>
                <Table.Td ta="right">{p.sets}</Table.Td>
                <Table.Td>{formatDateTime(p.updatedAt)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Modal opened={creating} onClose={() => setCreating(false)} title="Новая часть">
        <Stack gap="sm">
          <TextInput label="Название" required value={form.name} onChange={(e) => setForm({ ...form, name: e.currentTarget.value })} />
          <TextInput label="Объект" description="Как в разборе имён моделей — по нему подбираются модели" value={form.object}
            onChange={(e) => setForm({ ...form, object: e.currentTarget.value })} />
          <TextInput label="Часть здания" description="Например «корпус 1»" value={form.part} onChange={(e) => setForm({ ...form, part: e.currentTarget.value })} />
          <Group justify="flex-end">
            <Button disabled={!form.name.trim()} loading={create.isPending}
              onClick={() => create.mutate(form, { onSuccess: (created) => navigate(`/clash/parts/${created.id}`) })}>
              Создать
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Panel>
  );
}
