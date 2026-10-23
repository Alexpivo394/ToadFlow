import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { getToken } from '../api/client';

/** Какие запросы устаревают при изменении сущности на сервере. */
const affected: Record<string, string[]> = {
  jobs: ['jobs', 'runs', 'dashboard'],
  runs: ['runs', 'jobs', 'dashboard', 'schedules'],
  agents: ['agents', 'dashboard'],
  schedules: ['schedules', 'dashboard'],
};

/** Живые обновления: сервер шлёт changed(entity, id), страница перезапрашивает затронутые данные. */
export function useLiveUpdates(enabled: boolean): void {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!enabled) return;
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/ui', { accessTokenFactory: () => getToken() ?? '' })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => Math.min(30_000, 1000 * 2 ** context.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('changed', (entity: string) => {
      for (const key of affected[entity] ?? [entity]) queryClient.invalidateQueries({ queryKey: [key] });
    });

    connection.start().catch(() => {
      // Сервер недоступен — повторит автоматическое переподключение после первого успешного старта; данные всё равно грузятся запросами.
    });
    return () => {
      connection.stop();
    };
  }, [enabled, queryClient]);
}
