import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { Anchor, Badge, Button, Group, Loader, Table, Text, UnstyledButton } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { downloadFile } from '../../api/client';
import { useApi } from '../../api/queries';
import type { AnalyticsModelRow, AnalyticsParameterRow } from '../../api/types';
import { Empty, Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { toApiQuery } from './filters';
import { Delta, orNone, Share, useAnalyticsFilters } from './parts';

type SortKey = 'object' | 'fillRate' | 'delta' | 'checkedAt';

function Sortable({ label, sortKey, sort, onSort }: { label: string; sortKey: SortKey; sort: SortKey; onSort: (key: SortKey) => void }) {
  return (
    <Table.Th>
      <UnstyledButton onClick={() => onSort(sortKey)} fw={sort === sortKey ? 700 : 500} fz="sm">
        {label}{sort === sortKey ? ' ↓' : ''}
      </UnstyledButton>
    </Table.Th>
  );
}

const compare: Record<SortKey, (a: AnalyticsModelRow, b: AnalyticsModelRow) => number> = {
  object: (a, b) => orNone(a.object).localeCompare(orNone(b.object), 'ru') || orNone(a.discipline).localeCompare(orNone(b.discipline), 'ru')
    || a.modelName.localeCompare(b.modelName, 'ru'),
  fillRate: (a, b) => (a.fillRate ?? 2) - (b.fillRate ?? 2),
  delta: (a, b) => ((a.fillRate ?? 0) - (a.previousFillRate ?? a.fillRate ?? 0)) - ((b.fillRate ?? 0) - (b.previousFillRate ?? b.fillRate ?? 0)),
  checkedAt: (a, b) => b.checkedAt.localeCompare(a.checkedAt),
};

/** Все модели с последней проверкой в периоде: заполненность, изменение с прошлой проверки, откуда проверка. */
export function ModelsTab() {
  const [state] = useAnalyticsFilters();
  const query = toApiQuery(state);
  const models = useApi<AnalyticsModelRow[]>(['loi-analytics', 'models', query], `/api/loi/analytics/models?${query}`);
  const [sort, setSort] = useState<SortKey>('object');
  const rows = useMemo(() => [...(models.data ?? [])].sort(compare[sort]), [models.data, sort]);

  async function exportCsv() {
    try {
      await downloadFile(`/api/loi/analytics/models.csv?${query}`, 'LOI_модели.csv');
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  if (!models.data) return <Loader />;
  return (
    <Panel title={`Модели (${rows.length})`} actions={<Button size="xs" variant="default" onClick={exportCsv}>Выгрузить в Excel (CSV)</Button>}>
      {rows.length === 0 ? <Empty>Моделей нет</Empty> : (
        <Table.ScrollContainer minWidth={900}>
          <Table highlightOnHover fz="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Модель</Table.Th>
                <Sortable label="Объект · раздел" sortKey="object" sort={sort} onSort={setSort} />
                <Table.Th>Часть</Table.Th>
                <Sortable label="Заполненность" sortKey="fillRate" sort={sort} onSort={setSort} />
                <Table.Th>Полнота</Table.Th>
                <Sortable label="Изменение" sortKey="delta" sort={sort} onSort={setSort} />
                <Sortable label="Проверена" sortKey="checkedAt" sort={sort} onSort={setSort} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((m) => (
                <Table.Tr key={m.modelKey}>
                  <Table.Td>
                    <Anchor component={Link} to={`/analytics/model?key=${encodeURIComponent(m.modelKey)}`} fw={500}>{m.modelName}</Anchor>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c={m.object ? undefined : 'red'}>{orNone(m.object)}</Text>
                    <Text size="xs" c={m.discipline ? 'dimmed' : 'red'}>{orNone(m.discipline)}</Text>
                  </Table.Td>
                  <Table.Td c="dimmed">{m.part ?? ''}</Table.Td>
                  <Table.Td><Share value={m.fillRate} /></Table.Td>
                  <Table.Td><Share value={m.completeness} /></Table.Td>
                  <Table.Td><Delta now={m.fillRate} before={m.previousFillRate} profileChanged={m.profileChanged} /></Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap">
                      <Text size="sm">{formatDateTime(m.checkedAt)}</Text>
                      {m.source === 'Manual' && <Badge size="xs" variant="light" color="grape">вручную</Badge>}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Panel>
  );
}

/** Параметры по всем моделям среза: где и насколько их не заполняют. */
export function ParametersTab() {
  const [state] = useAnalyticsFilters();
  const query = toApiQuery(state);
  const parameters = useApi<AnalyticsParameterRow[]>(['loi-analytics', 'parameters', query], `/api/loi/analytics/parameters?${query}`);

  if (!parameters.data) return <Loader />;
  return (
    <Panel title={`Параметры (${parameters.data.length})`}>
      {parameters.data.length === 0 ? <Empty>Параметров нет</Empty> : (
        <Table highlightOnHover fz="sm">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Параметр</Table.Th>
              <Table.Th>Заполненность</Table.Th>
              <Table.Th>Моделей</Table.Th>
              <Table.Th>Значений</Table.Th>
              <Table.Th>Пусто</Table.Th>
              <Table.Th>Нет параметра</Table.Th>
              <Table.Th>Хуже всего</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {parameters.data.map((p) => (
              <Table.Tr key={p.name}>
                <Table.Td fw={500}>{p.name}</Table.Td>
                <Table.Td><Share value={p.fillRate} /></Table.Td>
                <Table.Td>{p.models}</Table.Td>
                <Table.Td>{p.total}</Table.Td>
                <Table.Td>{p.empty}</Table.Td>
                <Table.Td c={p.missing > 0 ? 'red.7' : undefined}>{p.missing}</Table.Td>
                <Table.Td c="dimmed">{p.worstModels.join(', ')}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Panel>
  );
}
