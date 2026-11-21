import { useState } from 'react';
import { Paper, Stack, Text } from '@mantine/core';
import { IconAlertTriangle, IconCheck, IconPoint, IconX } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import type { FeedItem } from '../../api/types';
import { moduleInfo, type ModuleName } from '../../spaces/modules';

const key = (item: FeedItem) => `${item.at}|${item.text}`;

/** Цвет значка: ошибка и потеря связи — красный, иначе цвет модуля, у ядра — серый. */
function color(item: FeedItem): string {
  if (item.tone === 'fail') return 'var(--mantine-color-red-6)';
  if (item.tone === 'warn') return 'var(--mantine-color-orange-6)';
  const module = item.module ? moduleInfo[item.module as ModuleName] : undefined;
  return module ? `var(--mantine-color-${module.color}-6)` : item.tone === 'ok' ? 'var(--mantine-color-green-6)' : 'var(--mantine-color-gray-6)';
}

const icons = { ok: IconCheck, warn: IconAlertTriangle, fail: IconX, info: IconPoint } as const;

/**
 * «Что происходит» — живая лента: готовые задания и рабочие события, новые сверху. Обновляется по живым событиям сервера; то, чего
 * не было в прошлом ответе, приезжает сверху с подсветкой.
 */
export function LiveFeed() {
  const feed = useApi<FeedItem[]>(['overview-feed'], '/api/overview/feed', { refetchInterval: 60_000 });
  // Новые строки — те, которых не было в прошлом ответе; первый ответ ничего не подсвечивает.
  const [shown, setShown] = useState<{ data: FeedItem[] | undefined; fresh: Set<string> }>({ data: undefined, fresh: new Set() });
  if (feed.data !== shown.data) {
    const before = shown.data ? new Set(shown.data.map(key)) : null;
    setShown({ data: feed.data, fresh: new Set(before ? (feed.data ?? []).map(key).filter((k) => !before.has(k)) : []) });
  }
  const items = feed.data ?? [];

  return (
    <Paper withBorder p="md" radius="lg" component="section" aria-label="Что происходит" style={{ flex: 1 }}>
      <Text fw={650} mb="xs">
        Что происходит <Text span size="xs" c="dimmed" fw={400}>· живая лента</Text>
      </Text>
      {feed.data && items.length === 0 ? (
        <Text c="dimmed" size="sm">Пока тихо</Text>
      ) : (
        <Stack gap={2} className="ov-feed-list">
          {items.map((item) => {
            const Icon = icons[item.tone] ?? IconPoint;
            return (
              <div key={key(item)} className="ov-feed-item" data-testid="feed-item" data-fresh={shown.fresh.has(key(item)) ? 'true' : 'false'}>
                <Text size="xs" c="dimmed" pt={2}>{new Date(item.at).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</Text>
                <span className="ov-feed-icon" style={{ background: color(item) }}><Icon size={14} stroke={2.5} /></span>
                <Text size="sm">{item.text}</Text>
              </div>
            );
          })}
        </Stack>
      )}
    </Paper>
  );
}
