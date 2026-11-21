import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { getToken } from '../api/client';
import { notificationsKey } from './api';

/** Личный хаб: сервер шлёт notification — колокольчик перечитывает счётчик и список. Открыт любой роли, в том числе проектировщику. */
export function useNotificationsLive(enabled: boolean): void {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!enabled) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/me', { accessTokenFactory: () => getToken() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    connection.on('notification', () => void queryClient.invalidateQueries({ queryKey: [notificationsKey] }));
    connection.onreconnected(() => void queryClient.invalidateQueries({ queryKey: [notificationsKey] }));
    const start = (attempt: number) => {
      connection.start().catch(() => {
        if (!stopped) timer = setTimeout(() => start(attempt + 1), Math.min(30_000, 1000 * 2 ** attempt));
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
