import { useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { Anchor, Button, Checkbox, Drawer, Group, Stack, Table, Text, Tooltip } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ReportRunView, RunCompare } from '../../../api/types';
import { formatFullDateTime } from './ReportPage';

const n = (value: number) => value.toLocaleString('ru-RU');
const NotComparable = 'Прогон до обновления — сравнение недоступно';

/** Сравнение двух прогонов по парам разделов: найдено в каждом и что между ними произошло; внизу итог. */
function CompareTable({ compare, runs }: { compare: RunCompare; runs: ReportRunView[] }) {
  const at = (runId: string) => formatFullDateTime(runs.find((r) => r.runId === runId)?.finishedAt);
  const total = compare.rows.reduce((s, r) => ({
    foundA: s.foundA + r.foundA, foundB: s.foundB + r.foundB, new: s.new + r.new, returned: s.returned + r.returned, fixed: s.fixed + r.fixed,
    goneByRules: s.goneByRules + r.goneByRules,
  }), { foundA: 0, foundB: 0, new: 0, returned: 0, fixed: 0, goneByRules: 0 });
  const cells = (r: typeof total) => [r.foundA, r.foundB, r.new, r.returned, r.fixed, r.goneByRules].map((v, i) => <Table.Td key={i} ta="right">{n(v)}</Table.Td>);
  return (
    <Table data-testid="run-compare" fz="xs" withTableBorder>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Пара</Table.Th>
          <Table.Th ta="right">Найдено в {at(compare.runA)}</Table.Th>
          <Table.Th ta="right">Найдено в {at(compare.runB)}</Table.Th>
          <Table.Th ta="right">Появилось</Table.Th>
          <Table.Th ta="right">Вернулось</Table.Th>
          <Table.Th ta="right">Исправлено</Table.Th>
          <Table.Th ta="right">Ушло из-за правил</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {compare.rows.map((r) => <Table.Tr key={r.pair}><Table.Td>{r.pair}</Table.Td>{cells(r)}</Table.Tr>)}
        <Table.Tr data-testid="run-compare-total" fw={700}><Table.Td>Итого</Table.Td>{cells(total)}</Table.Tr>
      </Table.Tbody>
    </Table>
  );
}

/**
 * «Все прогоны» проверки: удачные утверждённые прогоны, новые сверху, со сводкой; каждый ведёт в свой отчёт. Два отмеченных
 * прогона сравниваются по парам разделов; прогоны до обновления без счётчиков пар не сравниваются.
 */
export function RunsDrawer({ partId, opened, onClose }: { partId: string; opened: boolean; onClose: () => void }) {
  const runs = useApi<ReportRunView[]>(['clash-collisions', partId, 'runs'], opened ? `/api/clash/parts/${partId}/approved/runs` : null);
  const [picked, setPicked] = useState<string[]>([]);
  const [pair, setPair] = useState<[string, string] | null>(null);
  const compare = useApi<RunCompare>(['clash-collisions', partId, 'runs-compare', pair?.join('|')],
    pair ? `/api/clash/parts/${partId}/approved/runs/compare?a=${pair[0]}&b=${pair[1]}` : null);
  const toggle = (runId: string, checked: boolean) =>
    setPicked((p) => (checked ? [...p.filter((id) => id !== runId), runId].slice(-2) : p.filter((id) => id !== runId)));

  return (
    <Drawer opened={opened} onClose={onClose} position="right" size="xl" title="Прогоны утверждённой проверки">
      {runs.data && runs.data.length === 0 && <Text size="sm" c="dimmed">Удачных прогонов ещё не было</Text>}
      {runs.data && runs.data.length > 0 && (
        <Stack gap="sm">
          <Group gap="xs">
            <Button size="xs" disabled={picked.length !== 2} onClick={() => setPair([picked[0], picked[1]])}>Сравнить</Button>
            <Text size="xs" c="dimmed">Отметьте два прогона</Text>
          </Group>
          {pair && compare.data && <CompareTable compare={compare.data} runs={runs.data} />}
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th />
                <Table.Th>Прогон</Table.Th>
                <Table.Th>Версия</Table.Th>
                <Table.Th>Итог</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {runs.data.map((run) => {
                const comparable = run.comparable !== false;
                return (
                  <Table.Tr key={run.runId}>
                    <Table.Td>
                      <Tooltip label={NotComparable} disabled={comparable}>
                        <Checkbox size="xs" aria-label={`Сравнить прогон ${formatFullDateTime(run.finishedAt)}`} disabled={!comparable}
                          checked={picked.includes(run.runId)} onChange={(e) => toggle(run.runId, e.currentTarget.checked)} />
                      </Tooltip>
                    </Table.Td>
                    <Table.Td>
                      <Stack gap={0}>
                        <Anchor component={RouterLink} to={`/clash/reports/${run.runId}`} size="sm">{formatFullDateTime(run.finishedAt)}</Anchor>
                        <Text size="xs" c="dimmed">{run.trigger === 'schedule' ? 'по расписанию' : 'вручную'}{run.startedBy ? ` (${run.startedBy})` : ''}</Text>
                        {!comparable && <Text size="xs" c="dimmed">{NotComparable}</Text>}
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
                );
              })}
            </Table.Tbody>
          </Table>
        </Stack>
      )}
    </Drawer>
  );
}
