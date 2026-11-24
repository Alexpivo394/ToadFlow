import { Badge, Table, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import { QueryState } from '../../components/QueryState';
import { formatDateTime } from '../../lib/format';
import { t } from '../../i18n/ru';
import type { WaitCase, WaitGroup } from './api';
import { formatDuration, formatMemory } from './format';
import { MachineLink, PersonLink } from './parts';
import { withQuery } from './useUsageFilter';

const outcomeColor: Record<string, string> = { Succeeded: 'teal', Failed: 'red', Cancelled: 'gray' };

/** Отдельные случаи строки «Ожидания»: кто, когда, сколько, на какой машине, сколько было свободной памяти. До 200 последних. */
export function WaitCases({ group, rowKey, kind, query }: { group: WaitGroup; rowKey: string; kind?: string; query: string }) {
  const path = withQuery('/api/usage/waits/cases', query, { group, key: rowKey, kind });
  const cases = useApi<WaitCase[]>(['usage', 'cases', path], path);
  return (
    <QueryState query={cases}>
      {(rows) => (
        <Table.ScrollContainer minWidth={700}>
          <Table striped fz="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t.usage.when}</Table.Th>
                <Table.Th>{t.usage.kind}</Table.Th>
                <Table.Th>{t.usage.duration}</Table.Th>
                <Table.Th>{t.usage.outcome}</Table.Th>
                <Table.Th>{t.usage.person}</Table.Th>
                <Table.Th>{t.usage.machine}</Table.Th>
                <Table.Th>{t.usage.title}</Table.Th>
                <Table.Th>{t.usage.freeMemory}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((c) => (
                <Table.Tr key={c.id}>
                  <Table.Td>{formatDateTime(c.startedAt)}</Table.Td>
                  <Table.Td>{t.usage.kinds[c.kind]}</Table.Td>
                  <Table.Td>{formatDuration(c.durationMs)}</Table.Td>
                  <Table.Td><Badge variant="light" color={outcomeColor[c.outcome]}>{t.usage.outcomes[c.outcome]}</Badge></Table.Td>
                  <Table.Td><PersonLink id={c.user.id} name={c.user.name} /></Table.Td>
                  <Table.Td><MachineLink machineKey={c.machine.key} name={c.machine.name} /></Table.Td>
                  <Table.Td>
                    <Text size="sm" title={c.modelPath}>{c.modelName}</Text>
                    {c.hostModel && <Text size="xs" c="dimmed">{t.usage.hostModel}: {c.hostModel}</Text>}
                  </Table.Td>
                  <Table.Td>{formatMemory(c.freeMemoryMb)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </QueryState>
  );
}
