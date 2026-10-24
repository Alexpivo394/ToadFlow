import { Box, Button, Code, CopyButton, Group, Loader, Table } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { downloadFile } from '../../api/client';
import type { JobRow, LogLine } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { formatTime } from '../../lib/format';
import { formatShare, isExportResult, isLoiResult } from '../../lib/loi';

/** Главный файл результата: выгруженная модель или отчёт LOI. */
function resultFile(job: JobRow): string | null {
  const result = job.result;
  if (isExportResult(result)) return result.rvtPath ?? result.nwcPath;
  if (isLoiResult(result)) return result.reportPath;
  return null;
}

/** Папка результатов модели — из пути файла результата. */
function folderOf(path: string): string {
  const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return cut > 0 ? path.slice(0, cut) : path;
}

function downloadLog(job: JobRow) {
  return downloadFile(`/api/jobs/${job.id}/log.txt`, `${job.modelName.replace(/\.rvt$/i, '')}.log`);
}

function CopyPath({ value, label }: { value: string; label: string }) {
  return (
    <CopyButton value={value}>
      {({ copied, copy }) => (
        <Button size="compact-sm" variant="default" onClick={copy} title={value}>
          {copied ? 'Путь скопирован' : label}
        </Button>
      )}
    </CopyButton>
  );
}

/** Пошаговый лог модели и действия с ней; у проверки LOI — ещё итоги по правилам. */
export function JobLog({ job }: { job: JobRow }) {
  const log = useApi<LogLine[]>(['jobs', job.id, 'log'], `/api/jobs/${job.id}/log`);
  const retry = useApiMutation<void>('POST', () => `/api/jobs/${job.id}/retry`, { invalidate: ['runs'], success: 'Создан новый запуск для этой модели' });
  const cancel = useApiMutation<void>('POST', () => `/api/jobs/${job.id}/cancel`, { invalidate: ['runs', 'jobs'], success: 'Отмена отправлена' });
  const file = resultFile(job);
  const loi = isLoiResult(job.result) ? job.result : null;
  const finished = ['Succeeded', 'Warning', 'Failed', 'Cancelled'].includes(job.status);

  return (
    <Box>
      {loi && loi.rules.length > 0 && (
        <Table mb="xs" withTableBorder>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Правило</Table.Th>
              <Table.Th>Элементов</Table.Th>
              <Table.Th>Заполнены полностью</Table.Th>
              <Table.Th>Полнота</Table.Th>
              <Table.Th>Заполненность</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {loi.rules.map((rule, i) => (
              <Table.Tr key={i}>
                <Table.Td>{rule.title || `Правило ${i + 1}`}</Table.Td>
                <Table.Td>{rule.elements === 0 ? 'нет элементов' : rule.elements}</Table.Td>
                <Table.Td>{rule.elements === 0 ? '' : rule.completeElements}</Table.Td>
                <Table.Td>{formatShare(rule.completeness)}</Table.Td>
                <Table.Td>{formatShare(rule.fillRate)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {log.isLoading ? (
        <Loader size="sm" />
      ) : (
        <Code block style={{ whiteSpace: 'pre-wrap', maxHeight: 360, overflow: 'auto' }}>
          {(log.data ?? []).map((line) => `[${formatTime(line.at)}] ${line.stage ? line.stage + ': ' : ''}${line.text}`).join('\n') || 'Лог пуст'}
          {job.note ? `\nИтог: ${job.note}` : ''}
        </Code>
      )}
      <Group mt="xs" gap="xs">
        <Button size="compact-sm" variant="default" onClick={() => downloadLog(job)}>
          Скачать лог
        </Button>
        {file && <CopyPath value={folderOf(file)} label="Скопировать путь к папке" />}
        {loi?.reportPath && <CopyPath value={loi.reportPath} label="Скопировать путь к отчёту" />}
        <RequireRole role="Operator">
          {finished ? (
            <Button size="compact-sm" variant="default" onClick={() => retry.mutate()}>
              Повторить эту модель
            </Button>
          ) : (
            <Button size="compact-sm" variant="default" color="red" onClick={() => cancel.mutate()}>
              Отменить
            </Button>
          )}
        </RequireRole>
      </Group>
    </Box>
  );
}
