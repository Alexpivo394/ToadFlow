import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useQueryClient, type QueryKey } from '@tanstack/react-query';
import { getToken } from '../api/client';

const clashLists = ['clash-list', 'clash-collisions', 'clash-my', 'clash-checks', 'clash-analytics', 'clash-overview'];

/** Какие запросы устаревают при изменении сущности на сервере; <c>id</c> — id сущности из события. */
const affected: Record<string, (id: string) => QueryKey[]> = {
  jobs: () => [['jobs'], ['runs'], ['dashboard']],
  runs: () => [['runs'], ['jobs'], ['dashboard'], ['schedules']],
  agents: () => [['agents'], ['dashboard']],
  schedules: () => [['schedules'], ['dashboard']],
  // Черновик части и итог его прогона.
  'clash-parts': () => [['clash-parts']],
  // Ход прогона части — раз в несколько секунд: только состояние прогона, без списков.
  'clash-run-progress': (partId) => [['clash-parts', partId, 'run'], ['clash-collisions', partId, 'runs']],
  // Коллизии изменились (прогон, переходы, правки): все списки и счётчики коллизий.
  'clash-collisions': () => clashLists.map((key) => [key]),
};

/** Паузы повторов первого подключения: потом — автоматическое переподключение SignalR. */
const startDelaysMs = [1_000, 2_000, 5_000, 10_000, 30_000];

/**
 * Живые обновления: сервер шлёт changed(entity, id), страница перезапрашивает затронутые данные. Первое подключение повторяется,
 * пока не удастся (сервер мог перезапускаться); после переподключения перечитывается всё — события за время обрыва потеряны.
 */
export function useLiveUpdates(enabled: boolean): void {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/ui', { accessTokenFactory: () => getToken() ?? '' })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (context) => Math.min(30_000, 1000 * 2 ** context.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('changed', (entity: string, id: string) => {
      const keys = affected[entity]?.(id) ?? [[entity]];
      for (const queryKey of keys) void queryClient.invalidateQueries({ queryKey });
    });
    connection.onreconnected(() => void queryClient.invalidateQueries());

    const start = (attempt: number) => {
      connection.start().catch(() => {
        if (stopped) return;
        timer = setTimeout(() => start(attempt + 1), startDelaysMs[Math.min(attempt, startDelaysMs.length - 1)]);
      });
    };
    start(0);

    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
      void connection.stop();
    };
  }, [enabled, queryClient]);
}
