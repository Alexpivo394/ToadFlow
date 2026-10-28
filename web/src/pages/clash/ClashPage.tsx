import { Fragment, useState } from 'react';
import { useNavigate } from 'react-router';
import { Stack, Table, Tabs, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ClashModelView, ClashSnapshotView } from '../../api/types';
import { Empty, PageTitle, Panel, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatBytes, formatDateTime } from '../../lib/format';
import { CatalogPanel } from './CatalogPanel';
import { PartsList } from './PartsList';

const number = (value: number) => value.toLocaleString('ru-RU');

function SnapshotCells({ snapshot }: { snapshot: ClashSnapshotView }) {
  return (
    <>
      <Table.Td>{formatDateTime(snapshot.extractedAt)}</Table.Td>
      <Table.Td ta="right">{number(snapshot.elements)}</Table.Td>
      <Table.Td ta="right">{number(snapshot.triangles)}</Table.Td>
      <Table.Td ta="right">{formatBytes(snapshot.geometryBytes + snapshot.parametersBytes)}</Table.Td>
      <Table.Td>{snapshot.viewName} <YearBadge year={snapshot.revitYear} muted /></Table.Td>
    </>
  );
}

function History({ modelId }: { modelId: string }) {
  const snapshots = useApi<ClashSnapshotView[]>(['clash-models', modelId, 'snapshots'], `/api/clash/models/${modelId}/snapshots`);
  return (
    <>
      {(snapshots.data ?? []).map((s) => (
        <Table.Tr key={s.id} bg="var(--mantine-color-default-hover)">
          <Table.Td pl="xl"><Text size="xs" c="dimmed">выгрузка</Text></Table.Td>
          <SnapshotCells snapshot={s} />
          <Table.Td />
        </Table.Tr>
      ))}
    </>
  );
}

/** «Коллизии»: части проекта с черновиками проверок и выгрузки моделей. */
export function ClashPage({ tab }: { tab: 'parts' | 'snapshots' }) {
  const navigate = useNavigate();
  return (
    <>
      <PageTitle>{t.nav.clash}</PageTitle>
      <Tabs value={tab} onChange={(v) => navigate(v === 'snapshots' ? '/clash/snapshots' : '/clash')} keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="parts">Части</Tabs.Tab>
          <Tabs.Tab value="snapshots">Выгрузки</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="parts">
          <PartsList />
        </Tabs.Panel>
        <Tabs.Panel value="snapshots">
          <Snapshots />
        </Tabs.Panel>
      </Tabs>
    </>
  );
}

/** Выгрузки моделей для коллизий: последний слепок каждой модели, история слепков и каталог параметров. */
function Snapshots() {
  const models = useApi<ClashModelView[]>(['clash-models'], '/api/clash/models');
  const [open, setOpen] = useState<string | null>(null);

  return (
    <>
      <Stack gap="md">
        <Panel title="Выгрузки моделей">
          {(models.data ?? []).length === 0 ? (
            <Empty>Моделей ещё нет — создайте расписание «Выгрузка геометрии для коллизий»</Empty>
          ) : (
            <Table highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Модель</Table.Th>
                  <Table.Th>Выгружена</Table.Th>
                  <Table.Th ta="right">Элементов</Table.Th>
                  <Table.Th ta="right">Треугольников</Table.Th>
                  <Table.Th ta="right">Размер</Table.Th>
                  <Table.Th>Вид</Table.Th>
                  <Table.Th ta="right">Хранится</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {models.data!.map((m) => (
                  <Fragment key={m.id}>
                    <Table.Tr style={{ cursor: m.latest ? 'pointer' : undefined }} onClick={() => m.latest && setOpen(open === m.id ? null : m.id)}>
                      <Table.Td>
                        <Text size="sm">{m.name}</Text>
                        <Text size="xs" c="dimmed">{m.modelPath}</Text>
                      </Table.Td>
                      {m.latest ? (
                        <SnapshotCells snapshot={m.latest} />
                      ) : (
                        <Table.Td colSpan={5}><Text size="sm" c="dimmed">ещё не выгружалась</Text></Table.Td>
                      )}
                      <Table.Td ta="right">{m.snapshotCount}</Table.Td>
                    </Table.Tr>
                    {open === m.id && <History modelId={m.id} />}
                  </Fragment>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </Panel>
        <CatalogPanel models={models.data ?? []} />
      </Stack>
    </>
  );
}
