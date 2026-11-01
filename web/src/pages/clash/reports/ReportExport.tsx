import { useState } from 'react';
import { Button, Group, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch, downloadFile } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { ReportArchiveView, ReportView } from '../../../api/types';
import { formatFullDateTime } from './ReportPage';
import { inScope, type ReportSelection } from './ReportTree';

const currentKey = ['clash-collisions', 'report-archive-current'];

/** «1 пара», «2 пары», «5 пар». */
function pairsWord(count: number) {
  const tens = count % 100;
  const ones = count % 10;
  const word = tens >= 11 && tens <= 14 ? 'пар' : ones === 1 ? 'пара' : ones >= 2 && ones <= 4 ? 'пары' : 'пар';
  return `${count} ${word}`;
}

const megabytes = (bytes: number) => `${Math.max(1, Math.round(bytes / 1024 / 1024)).toLocaleString('ru-RU')} МБ`;

/** Ход архива этого отчёта: очередь, сборка с отменой, готов — скачать, ошибка или устарел. */
function ArchiveStatus({ archive, fileName, onCancel }: { archive: ReportArchiveView; fileName: string; onCancel: () => void }) {
  const [downloading, setDownloading] = useState(false);
  const download = async () => {
    setDownloading(true);
    try {
      await downloadFile(`/api/clash/report-archives/${archive.id}/download`, fileName);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось скачать', message: (error as Error).message });
    } finally {
      setDownloading(false);
    }
  };
  switch (archive.state) {
    case 'queued':
      return (
        <Group gap="xs">
          <Text size="xs" c="dimmed">{archive.queuePosition > 0 ? `Архив в очереди: перед ним ${archive.queuePosition}` : 'Архив в очереди'}</Text>
          <Button size="compact-xs" variant="subtle" onClick={onCancel}>Отменить</Button>
        </Group>
      );
    case 'running':
      return (
        <Group gap="xs">
          <Text size="xs" c="dimmed">
            Готовим архив: {archive.pairsDone} из {archive.pairsTotal} {archive.pairsTotal % 10 === 1 && archive.pairsTotal % 100 !== 11 ? 'пары' : 'пар'}
          </Text>
          <Button size="compact-xs" variant="subtle" onClick={onCancel}>Отменить</Button>
        </Group>
      );
    case 'ready':
      return (
        <Group gap="xs">
          <Button size="xs" loading={downloading} onClick={download}>Скачать архив ({megabytes(archive.sizeBytes ?? 0)})</Button>
          {archive.expiresAt && <Text size="xs" c="dimmed">до {formatFullDateTime(archive.expiresAt)}</Text>}
        </Group>
      );
    case 'failed':
      return <Text size="xs" c="red">Архив не собрался: {archive.error ?? 'ошибка сервера'}</Text>;
    default:
      return <Text size="xs" c="dimmed">Архив устарел — выгрузите заново</Text>;
  }
}

/**
 * Выгрузки отчёта — того, что выбрано в дереве, с фильтрами списка: Excel одним файлом; HTML пары — одним файлом, раздела или
 * всего отчёта — архивом по парам, который сервер готовит в фоне (ход опрашивается раз в 2 с).
 */
export function ReportExport({ runId, view, selection, filters, total, fileBase }: {
  runId: string;
  view: ReportView;
  selection: ReportSelection;
  /** Область, выбор и фильтры списка — как в адресе. */
  filters: URLSearchParams;
  /** Строк в выборке пары — для подписи кнопки. */
  total: number | undefined;
  /** Начало имени файла: «Коллизии Полковая Корпус 1». */
  fileBase: string;
}) {
  const client = useQueryClient();
  const [busy, setBusy] = useState<string | null>(null);
  // Опрос раз в 2 с, пока архив в очереди или собирается; состояние берётся из кеша — тот же запрос, прошлый ответ.
  const ours = (a: ReportArchiveView | null | undefined) => (a && a.runId === runId ? a : null);
  const cached = ours(client.getQueryData<ReportArchiveView | null>(currentKey));
  const polling = cached?.state === 'queued' || cached?.state === 'running';
  const current = useApi<ReportArchiveView | null>(currentKey, '/api/clash/report-archives/current', { refetchInterval: polling ? 2000 : undefined });
  const archive = ours(current.data);

  const scope = filters.get('scope') ?? 'changes';
  const day = formatFullDateTime(view.run.finishedAt).slice(0, 10);
  const pair = selection.kind === 'pair' ? view.pairs.find((p) => p.pair === selection.pair) : undefined;
  const archivePairs = selection.kind === 'discipline'
    ? (view.disciplines.find((d) => d.name === selection.name)?.pairs ?? []).filter((key) => {
      const p = view.pairs.find((x) => x.pair === key);
      return p !== undefined && inScope(p, scope);
    }).length
    : view.pairs.filter((p) => inScope(p, scope)).length;

  const query = () => {
    const q = new URLSearchParams(filters);
    q.set('baseUrl', window.location.origin);
    return q;
  };

  const download = async (format: 'xlsx' | 'html', label: string | null) => {
    setBusy(format);
    try {
      await downloadFile(`/api/clash/reports/${runId}/export.${format}?${query()}`, `${[fileBase, label, day].filter(Boolean).join(' ')}.${format}`);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось выгрузить', message: (error as Error).message });
    } finally {
      setBusy(null);
    }
  };

  const startArchive = async () => {
    setBusy('archive');
    try {
      const started = await apiFetch<ReportArchiveView>(`/api/clash/reports/${runId}/archive?${query()}`, { method: 'POST' });
      client.setQueryData(currentKey, started);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось начать архив', message: (error as Error).message });
    } finally {
      setBusy(null);
    }
  };

  const cancel = async () => {
    if (!archive) return;
    try {
      await apiFetch(`/api/clash/report-archives/${archive.id}`, { method: 'DELETE' });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось отменить', message: (error as Error).message });
    }
    await client.invalidateQueries({ queryKey: currentKey });
  };

  const selectionLabel = selection.kind === 'pair' ? pair?.label ?? selection.pair : selection.kind === 'discipline' ? selection.name : null;
  const archiveName = `${[fileBase, archive?.discipline ?? 'все пары', day].filter(Boolean).join(' ')}.zip`;

  return (
    <Group gap="xs" justify="flex-end">
      {archive && <ArchiveStatus archive={archive} fileName={archiveName} onCancel={cancel} />}
      <Button size="xs" variant="default" loading={busy === 'xlsx'} onClick={() => download('xlsx', selectionLabel)}>Excel</Button>
      {selection.kind === 'pair' ? (
        <Button size="xs" variant="default" loading={busy === 'html'} onClick={() => download('html', selectionLabel)}>
          HTML: {pair?.label ?? selection.pair}{total !== undefined ? ` (${total.toLocaleString('ru-RU')})` : ''}
        </Button>
      ) : (
        <Button size="xs" variant="default" loading={busy === 'archive'} disabled={polling || archivePairs === 0} onClick={startArchive}>
          HTML: {selection.kind === 'discipline' ? `раздел ${selection.name}` : 'всё'} — архив ({pairsWord(archivePairs)})
        </Button>
      )}
    </Group>
  );
}
