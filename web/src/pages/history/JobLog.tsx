import { Box, Button, Code, CopyButton, Group, Loader } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { getToken } from '../../api/client';
import type { JobRow, LogLine } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { formatTime } from '../../lib/format';

/** Папка результатов модели — из пути выгруженного файла. */
function outputFolder(job: JobRow): string | null {
  const path = job.result?.rvtPath ?? job.result?.nwcPath;
  if (!path) return null;
  const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return cut > 0 ? path.slice(0, cut) : path;
}

async function downloadLog(job: JobRow) {
  const response = await fetch(`/api/jobs/${job.id}/log.txt`, { headers: { Authorization: `Bearer ${getToken()}` } });
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = Object.assign(document.createElement('a'), { href: url, download: `${job.modelName.replace(/\.rvt$/i, '')}.log` });
  link.click();
  URL.revokeObjectURL(url);
}

/** Пошаговый лог модели и действия с ней. */
export function JobLog({ job }: { job: JobRow }) {
  const log = useApi<LogLine[]>(['jobs', job.id, 'log'], `/api/jobs/${job.id}/log`);
  const retry = useApiMutation<void>('POST', () => `/api/jobs/${job.id}/retry`, { invalidate: ['runs'], success: 'Создан новый запуск для этой модели' });
  const cancel = useApiMutation<void>('POST', () => `/api/jobs/${job.id}/cancel`, { invalidate: ['runs', 'jobs'], success: 'Отмена отправлена' });
  const folder = outputFolder(job);
  const finished = ['Succeeded', 'Warning', 'Failed', 'Cancelled'].includes(job.status);

  return (
    <Box>
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
        {folder && (
          <CopyButton value={folder}>
            {({ copied, copy }) => (
              <Button size="compact-sm" variant="default" onClick={copy} title={folder}>
                {copied ? 'Путь скопирован' : 'Скопировать путь к папке'}
              </Button>
            )}
          </CopyButton>
        )}
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
