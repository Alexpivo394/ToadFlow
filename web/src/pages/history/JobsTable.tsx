import { Fragment, useState } from 'react';
import { Anchor, Badge, Table, Text } from '@mantine/core';
import { Link } from 'react-router';
import type { JobRow, StepStatus } from '../../api/types';
import { JobStatusBadge } from '../../components/ui';
import { formatBytes, formatDuration } from '../../lib/format';
import { formatShare, isCatalogResult, isExportResult, isLoiResult } from '../../lib/loi';
import { JobLog } from './JobLog';

function Step({ status, size }: { status: StepStatus | undefined; size: number | null | undefined }) {
  if (!status || status === 'NotRequested') return <Text c="dimmed">—</Text>;
  if (status === 'Done') return <Text c="green">✓ {formatBytes(size)}</Text>;
  if (status === 'Skipped') return <Text c="yellow.7">⚠ пропущен</Text>;
  return <Text c="red">✗</Text>;
}

/** Доля с цветом: от 95 % — зелёный, от 70 % — жёлтый, ниже — красный. */
function Share({ value }: { value: number | null | undefined }) {
  if (value == null) return <Text c="dimmed">—</Text>;
  return <Text c={value >= 0.95 ? 'green' : value >= 0.7 ? 'yellow.7' : 'red'}>{formatShare(value)}</Text>;
}

/** Колонки результата: у выгрузки — RVT и NWC, у проверки LOI — полнота и заполненность, вперемешку — одна общая. */
type Columns = 'export' | 'loi' | 'mixed';

function columnsOf(jobs: JobRow[]): Columns {
  if (jobs.every((job) => job.moduleKind === 'export')) return 'export';
  if (jobs.every((job) => job.moduleKind !== 'export')) return 'loi';
  return 'mixed';
}

/** Итог задания одной строкой — для общей колонки и каталога. */
function summary(job: JobRow): string | null {
  const result = job.result;
  if (isExportResult(result)) {
    const parts = [result.rvt === 'Done' && `RVT ${formatBytes(result.rvtSizeBytes)}`, result.nwc === 'Done' && `NWC ${formatBytes(result.nwcSizeBytes)}`];
    return parts.filter(Boolean).join(' · ') || null;
  }
  if (isLoiResult(result)) return `полнота ${formatShare(result.completeness)} · заполненность ${formatShare(result.fillRate)}`;
  if (isCatalogResult(result)) return `категорий ${result.categories.length}, параметров ${result.parameters.length}`;
  return null;
}

function ResultCells({ job, columns }: { job: JobRow; columns: Columns }) {
  const result = job.result;
  if (columns === 'export') {
    const exported = isExportResult(result) ? result : null;
    return (
      <>
        <Table.Td><Step status={exported?.rvt} size={exported?.rvtSizeBytes} /></Table.Td>
        <Table.Td><Step status={exported?.nwc} size={exported?.nwcSizeBytes} /></Table.Td>
      </>
    );
  }
  if (columns === 'loi' && !isCatalogResult(result)) {
    const checked = isLoiResult(result) ? result : null;
    return (
      <>
        <Table.Td><Share value={checked?.completeness} /></Table.Td>
        <Table.Td><Share value={checked?.fillRate} /></Table.Td>
      </>
    );
  }
  return <Table.Td colSpan={2}><Text size="sm" c="dimmed">{summary(job) ?? '—'}</Text></Table.Td>;
}

const headers: Record<Columns, [string, string] | [string]> = {
  export: ['RVT', 'NWC'],
  loi: ['Полнота', 'Заполненность'],
  mixed: ['Результат'],
};

/** Модели запуска: результат, где обрабатывалась, сколько заняла; клик раскрывает лог. */
export function JobsTable({ jobs, showRun, initiallyOpen }: { jobs: JobRow[]; showRun?: boolean; initiallyOpen?: string | null }) {
  const [open, setOpen] = useState<string | null>(initiallyOpen ?? null);
  const columns = columnsOf(jobs);
  const titles = headers[columns];

  return (
    <Table.ScrollContainer minWidth={900}>
      <Table highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            {showRun && <Table.Th>Запуск</Table.Th>}
            <Table.Th>Модель</Table.Th>
            <Table.Th>Статус</Table.Th>
            {titles.map((title) => (
              <Table.Th key={title} colSpan={titles.length === 1 ? 2 : undefined}>
                {title}
              </Table.Th>
            ))}
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
                    <Anchor component={Link} to={`/history/runs/${job.runId}`} size="sm" onClick={(e) => e.stopPropagation()}>
                      {job.runName}
                    </Anchor>
                  </Table.Td>
                )}
                <Table.Td fw={500}>{job.modelName}</Table.Td>
                <Table.Td>
                  <JobStatusBadge status={job.status} />
                  {job.waitingForRevit && <Badge ml={4} color="yellow" variant="light">ждёт Revit {job.waitingForRevit}</Badge>}
                </Table.Td>
                <ResultCells job={job} columns={columns} />
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
