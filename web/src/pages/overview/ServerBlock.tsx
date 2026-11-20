import { Paper, Stack, Text, Title } from '@mantine/core';
import type { ServerStats } from '../../api/types';
import { formatBytes, formatRelative } from '../../lib/format';

/** Сколько работает сервер: «12 д», «5 ч», «20 мин». */
function uptime(startedAt: string): string {
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 60_000));
  if (minutes < 60) return `${minutes} мин`;
  if (minutes < 48 * 60) return `${Math.floor(minutes / 60)} ч`;
  return `${Math.floor(minutes / 1440)} д`;
}

/** Сведения о сервере: версия, время работы, база, место на томах, память, рабочие процессы модулей. */
export function ServerFacts({ stats }: { stats: ServerStats }) {
  return (
    <Stack gap={4}>
      <Text size="sm">Версия {stats.version} · работает {uptime(stats.startedAt)}</Text>
      <Text size="sm">База {formatBytes(stats.databaseBytes)} · память сервера {formatBytes(stats.processMemoryBytes)}</Text>
      {stats.volumes.map((volume) => (
        <Text key={volume.name} size="sm" c={volume.freeBytes < volume.totalBytes * 0.1 ? 'red' : undefined}>
          {volume.name}: свободно {formatBytes(volume.freeBytes)} из {formatBytes(volume.totalBytes)}
        </Text>
      ))}
      {stats.workers.map((worker) => (
        <Text key={worker.module + worker.title} size="sm" c={worker.online ? undefined : 'red'}>
          {worker.title}: {worker.online ? 'в сети' : `не на связи${worker.lastSeenAt ? ` (последний раз ${formatRelative(worker.lastSeenAt)})` : ''}`}
        </Text>
      ))}
    </Stack>
  );
}

export function ServerBlock({ stats }: { stats: ServerStats | undefined }) {
  return (
    <Paper withBorder p="md" radius="md" component="section" aria-label="Сервер">
      <Title order={4} mb="xs">Сервер</Title>
      {stats ? <ServerFacts stats={stats} /> : <Text c="dimmed" size="sm">—</Text>}
    </Paper>
  );
}
