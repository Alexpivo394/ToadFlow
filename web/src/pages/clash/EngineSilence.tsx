import { Alert } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { EngineStatus } from '../../api/types';

/** Через сколько минут молчания движка при ждущих прогонах показывается предупреждение. */
export const SilentAfterMinutes = 5;

/**
 * Сколько минут движок молчит, если это уже повод тревожиться: есть ждущие прогоны, а движок не обращался к серверу дольше
 * 5 минут (или ни разу с запуска сервера, а сервер работает дольше 5 минут). Иначе null.
 */
export function silentMinutes(status: EngineStatus | null | undefined, now: Date = new Date()): number | null {
  if (!status || status.queued === 0) return null;
  const since = new Date(status.lastSeenAt ?? status.serverStartedAt).getTime();
  const minutes = Math.floor((now.getTime() - since) / 60_000);
  return minutes >= SilentAfterMinutes ? minutes : null;
}

/** Состояние движка; пока что-то ждёт — раз в минуту. */
export function useEngineSilence(enabled: boolean): number | null {
  const status = useApi<EngineStatus>(['clash-engine', 'status'], enabled ? '/api/clash/engine/status' : null, { refetchInterval: 60_000 });
  return enabled ? silentMinutes(status.data) : null;
}

/** «Движок не отвечает N мин»: прогоны стоят в очереди, а движок коллизий не забирает их — проверьте контейнер clash-engine. */
export function EngineSilentAlert({ minutes }: { minutes: number | null }) {
  if (minutes == null) return null;
  return (
    <Alert color="red" title={`Движок не отвечает ${minutes} мин`}>
      Прогоны ждут в очереди, но движок коллизий их не забирает. Администратору: проверьте контейнер clash-engine
      (docker compose ps, docker compose logs clash-engine).
    </Alert>
  );
}
