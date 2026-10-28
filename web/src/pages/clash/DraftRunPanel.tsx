import { Alert, Badge, Button, Group, Stack, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { DraftRunSummary, EngineRunState } from '../../api/types';
import { formatBytes, formatDateTime, formatDuration } from '../../lib/format';
import { DraftResults } from './DraftResults';

const stateLabel: Record<EngineRunState, { label: string; color: string }> = {
  Queued: { label: 'в очереди', color: 'gray' },
  Running: { label: 'выполняется', color: 'blue' },
  Succeeded: { label: 'готово', color: 'green' },
  Failed: { label: 'ошибка', color: 'red' },
  Cancelled: { label: 'отменён', color: 'gray' },
};

/** Прогон черновика: «Прогнать», состояние и ход, итоги по времени и памяти, результаты последнего удачного прогона. */
export function DraftRunPanel({ partId, readOnly, dirty }: { partId: string; readOnly: boolean; dirty: boolean }) {
  const run = useApi<DraftRunSummary | null>(['clash-parts', partId, 'run'], `/api/clash/parts/${partId}/draft/run`);
  const start = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/draft/run`, { invalidate: ['clash-parts'] });
  const cancel = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/draft/run/cancel`, { invalidate: ['clash-parts'] });
  const current = run.data ?? null;
  const active = current?.state === 'Queued' || current?.state === 'Running';
  const stats = current?.stats;

  return (
    <Stack gap="md">
      <Group gap="sm">
        {!readOnly && (
          <Button onClick={() => start.mutate()} loading={start.isPending}>
            Прогнать
          </Button>
        )}
        {!readOnly && active && (
          <Button variant="default" onClick={() => cancel.mutate()} loading={cancel.isPending}>
            Отменить
          </Button>
        )}
        {current && <Badge color={stateLabel[current.state].color} variant="light">{stateLabel[current.state].label}</Badge>}
        {current?.stage && <Text size="sm">{current.stage}</Text>}
        {current && <Text size="xs" c="dimmed">запущен {formatDateTime(current.createdAt)}</Text>}
      </Group>
      {dirty && !readOnly && <Text size="sm" c="orange">Черновик не сохранён: прогон возьмёт сохранённую версию.</Text>}
      {current?.state === 'Failed' && current.error && <Alert color="red" title="Прогон не удался">{current.error}</Alert>}
      {current?.state === 'Succeeded' && stats && (
        <Text size="sm" c="dimmed">
          Элементов {stats.elements.toLocaleString('ru-RU')}, треугольников {stats.triangles.toLocaleString('ru-RU')}; загрузка {formatDuration(stats.loadSeconds)},
          поиск {formatDuration(stats.broadSeconds + stats.narrowSeconds)}; память движка до {formatBytes(stats.peakWorkingSetBytes)}
        </Text>
      )}
      <DraftResults partId={partId} />
    </Stack>
  );
}
