import { Fragment, useState } from 'react';
import { Anchor, Badge, Table, Text } from '@mantine/core';
import { Link } from 'react-router';
import type { JobRow, StepStatus } from '../../api/types';
import { JobStatusBadge } from '../../components/ui';
import { formatBytes, formatDuration } from '../../lib/format';
import { JobLog } from './JobLog';

function Step({ status, size }: { status: StepStatus | undefined; size: number | null | undefined }) {
  if (!status || status === 'NotRequested') return <Text c="dimmed">—</Text>;
  if (status === 'Done') return <Text c="green">✓ {formatBytes(size)}</Text>;
  if (status === 'Skipped') return <Text c="yellow.7">⚠ пропущен</Text>;
  return <Text c="red">✗</Text>;
}

/** Модели запуска: результат по форматам, где выгружалась, сколько заняла; клик раскрывает лог. */
export function JobsTable({ jobs, showRun, initiallyOpen }: { jobs: JobRow[]; showRun?: boolean; initiallyOpen?: string | null }) {
  const [open, setOpen] = useState<string | null>(initiallyOpen ?? null);

  return (
    <Table.ScrollContainer minWidth={900}>
      <Table highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            {showRun && <Table.Th>Запуск</Table.Th>}
            <Table.Th>Модель</Table.Th>
            <Table.Th>Статус</Table.Th>
            <Table.Th>RVT</Table.Th>
            <Table.Th>NWC</Table.Th>
            <Table.Th>Где</Table.Th>
            <Table.Th>Время</Table.Th>
            <Table.Th>Примечание</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {jobs.map((job) => (
            <Fragment key={job.id}>
              <Table.Tr onClick={() => setOpen(open === job.id ? null : job.id)} style={{ cursor: 'pointer' }}
                bg={open === job.id ? 'var(--mantine-color-default-hover)' : undefined}>
                {showRun && (
                  <Table.Td>
                    <Anchor component={Link} to={`/history/runs/${job.runId}`} onClick={(e) => e.stopPropagation()}>
                      {job.runName}
                    </Anchor>
                  </Table.Td>
                )}
                <Table.Td fw={500}>{job.modelName}</Table.Td>
                <Table.Td>
                  <JobStatusBadge status={job.status} />
                  {job.waitingForRevit && <Badge ml={4} color="yellow" variant="light">ждёт Revit {job.waitingForRevit}</Badge>}
                </Table.Td>
                <Table.Td><Step status={job.result?.rvt} size={job.result?.rvtSizeBytes} /></Table.Td>
                <Table.Td><Step status={job.result?.nwc} size={job.result?.nwcSizeBytes} /></Table.Td>
                <Table.Td c="dimmed">
                  {[job.agentName, job.revitYear ? `R${job.revitYear}` : null, job.slot ? `#${job.slot}` : null].filter(Boolean).join(' · ')}
                </Table.Td>
                <Table.Td>{formatDuration(job.durationSeconds)}</Table.Td>
                <Table.Td>
                  <Text size="sm" c={job.status === 'Failed' ? 'red' : job.status === 'Warning' ? 'yellow.7' : 'dimmed'} lineClamp={2}>
                    {job.note}
                    {job.maxAttempts > 1 && job.attempt > 1 ? ` · попытка ${job.attempt} из ${job.maxAttempts}` : ''}
                  </Text>
                </Table.Td>
              </Table.Tr>
              {open === job.id && (
                <Table.Tr>
                  <Table.Td colSpan={showRun ? 8 : 7}>
                    <JobLog job={job} />
                  </Table.Td>
                </Table.Tr>
              )}
            </Fragment>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
