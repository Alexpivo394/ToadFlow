import { Link as RouterLink } from 'react-router';
import { Anchor, Drawer, Stack, Table, Text } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ReportRunView } from '../../../api/types';
import { formatFullDateTime } from './ReportPage';

const n = (value: number) => value.toLocaleString('ru-RU');

/** «Все прогоны» проверки: удачные утверждённые прогоны, новые сверху, со сводкой; каждый ведёт в свой отчёт. */
export function RunsDrawer({ partId, opened, onClose }: { partId: string; opened: boolean; onClose: () => void }) {
  const runs = useApi<ReportRunView[]>(['clash-collisions', partId, 'runs'], opened ? `/api/clash/parts/${partId}/approved/runs` : null);

  return (
    <Drawer opened={opened} onClose={onClose} position="right" size="lg" title="Прогоны утверждённой проверки">
      {runs.data && runs.data.length === 0 && <Text size="sm" c="dimmed">Удачных прогонов ещё не было</Text>}
      {runs.data && runs.data.length > 0 && (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Прогон</Table.Th>
              <Table.Th>Версия</Table.Th>
              <Table.Th>Итог</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {runs.data.map((run) => (
              <Table.Tr key={run.runId}>
                <Table.Td>
                  <Stack gap={0}>
                    <Anchor component={RouterLink} to={`/clash/reports/${run.runId}`} size="sm">{formatFullDateTime(run.finishedAt)}</Anchor>
                    <Text size="xs" c="dimmed">{run.trigger === 'schedule' ? 'по расписанию' : 'вручную'}{run.startedBy ? ` (${run.startedBy})` : ''}</Text>
                  </Stack>
                </Table.Td>
                <Table.Td>v{run.version}</Table.Td>
                <Table.Td>
                  {run.summary && (
                    <Text size="xs">
                      новых {n(run.summary.new)}, вернулись {n(run.summary.returned)}, активных {n(run.summary.active)}, исправлено {n(run.summary.fixed)}
                    </Text>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Drawer>
  );
}
