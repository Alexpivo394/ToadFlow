import { Group, Paper, Stack, Text } from '@mantine/core';
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

/** Кольцо занятости тома: зелёное, с 80 % — оранжевое, с 90 % — красное. */
function DiskRing({ used }: { used: number }) {
  const color = used >= 90 ? 'red' : used >= 80 ? 'orange' : 'green';
  const circumference = 2 * Math.PI * 15;
  return (
    <div style={{ position: 'relative', width: 64, height: 64, flexShrink: 0 }}>
      <svg viewBox="0 0 36 36" width={64} height={64} aria-hidden>
        <circle cx="18" cy="18" r="15" fill="none" stroke="var(--mantine-color-default-border)" strokeWidth="4" />
        <circle cx="18" cy="18" r="15" fill="none" stroke={`var(--mantine-color-${color}-6)`} strokeWidth="4" strokeLinecap="round"
          strokeDasharray={`${(used / 100) * circumference} ${circumference}`} transform="rotate(-90 18 18)" />
      </svg>
      <Text size="sm" fw={600} style={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center' }}>{used} %</Text>
    </div>
  );
}

/** Сервер на «Обзоре», компактно: самый занятый том кольцом, версия и время работы, рабочие процессы модулей. */
export function ServerBlock({ stats }: { stats: ServerStats | undefined }) {
  const fullest = stats?.volumes.filter((v) => v.totalBytes > 0)
    .reduce<ServerStats['volumes'][number] | null>((worst, v) => (!worst || v.freeBytes / v.totalBytes < worst.freeBytes / worst.totalBytes ? v : worst), null);
  return (
    <Paper withBorder p="md" radius="lg" component="section" aria-label="Сервер">
      <Text fw={650} mb="xs">Сервер</Text>
      {stats ? (
        <Group gap="lg" wrap="wrap">
          {fullest && (
            <Group gap="sm" wrap="nowrap">
              <DiskRing used={Math.round((1 - fullest.freeBytes / fullest.totalBytes) * 100)} />
              <div>
                <Text size="sm" fw={600}>{fullest.name}</Text>
                <Text size="xs" c="dimmed">свободно {formatBytes(fullest.freeBytes)} из {formatBytes(fullest.totalBytes)}</Text>
              </div>
            </Group>
          )}
          <div>
            <Text size="sm" fw={600}>{stats.version}</Text>
            <Text size="xs" c="dimmed">работает {uptime(stats.startedAt)}</Text>
          </div>
          {stats.workers.map((worker) => (
            <Group key={worker.module + worker.title} gap={8} wrap="nowrap">
              <span className="ov-dot" data-tone={worker.online ? 'live' : 'bad'} />
              <div>
                <Text size="sm" fw={600}>{worker.title}</Text>
                <Text size="xs" c={worker.online ? 'dimmed' : 'red'}>
                  {worker.online ? 'в сети' : `не на связи${worker.lastSeenAt ? ` ${formatRelative(worker.lastSeenAt)}` : ''}`}
                </Text>
              </div>
            </Group>
          ))}
        </Group>
      ) : (
        <Text c="dimmed" size="sm">—</Text>
      )}
    </Paper>
  );
}
