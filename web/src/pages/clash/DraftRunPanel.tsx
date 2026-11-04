import { useState } from 'react';
import { Alert, Badge, Button, Group, Stack, Text, Tooltip } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { ApprovedVersionView, DraftRunSummary, EngineRunState } from '../../api/types';
import { askConfirm } from '../../components/ui';
import { formatBytes, formatDateTime, formatDuration } from '../../lib/format';
import { ApproveDialog } from './approved/ApproveDialog';
import { EngineSilentAlert, useEngineSilence } from './EngineSilence';

export const stateLabel: Record<EngineRunState, { label: string; color: string }> = {
  Queued: { label: 'в очереди', color: 'gray' },
  Running: { label: 'выполняется', color: 'blue' },
  Succeeded: { label: 'готово', color: 'green' },
  Failed: { label: 'ошибка', color: 'red' },
  Cancelled: { label: 'отменён', color: 'gray' },
};

/** «≈ 2 мин», «≈ 1 ч 10 мин». */
function about(seconds: number): string {
  const minutes = Math.max(1, Math.round(seconds / 60));
  return minutes < 60 ? `≈ ${minutes} мин` : `≈ ${Math.floor(minutes / 60)} ч${minutes % 60 ? ` ${minutes % 60} мин` : ''}`;
}

/** Очередь и обычная длительность: «В очереди: 3-й · обычно ≈ 2 мин»; у идущего — «обычно ≈ 2 мин»; иначе пусто. */
export function queueText(run: DraftRunSummary | null | undefined): string {
  if (!run || (run.state !== 'Queued' && run.state !== 'Running')) return '';
  const parts = [run.state === 'Queued' && run.queuePosition ? `В очереди: ${run.queuePosition}-й` : null,
    run.typicalSeconds ? `обычно ${about(run.typicalSeconds)}` : null];
  return parts.filter(Boolean).join(' · ');
}

/** Прогон черновика: «Прогнать», состояние и ход, итоги по времени и памяти, результаты последнего удачного прогона. */
export function DraftRunPanel({ partId, readOnly, dirty, onSave }: { partId: string; readOnly: boolean; dirty: boolean; onSave?: () => Promise<void> }) {
  const run = useApi<DraftRunSummary | null>(['clash-parts', partId, 'run'], `/api/clash/parts/${partId}/draft/run`);
  const start = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/draft/run`, { invalidate: ['clash-parts'] });
  const cancel = useApiMutation<void>('POST', () => `/api/clash/parts/${partId}/draft/run/cancel`, { invalidate: ['clash-parts'] });
  const current = run.data ?? null;
  const active = current?.state === 'Queued' || current?.state === 'Running';
  const stats = current?.stats;
  const versions = useApi<ApprovedVersionView[]>(['clash-parts', partId, 'versions'], `/api/clash/parts/${partId}/versions`);
  const [approving, setApproving] = useState(false);
  const silent = useEngineSilence(active);
  // Сводная строка — время прогона; число коллизий, элементы, время этапов и память — в подсказке.
  const details = current?.state === 'Succeeded'
    ? [current.hitCount != null ? `${current.hitCount.toLocaleString('ru-RU')} коллизий` : null,
      stats ? `Элементов ${stats.elements.toLocaleString('ru-RU')}, треугольников ${stats.triangles.toLocaleString('ru-RU')}; загрузка ${formatDuration(stats.loadSeconds)}, `
        + `поиск ${formatDuration(stats.broadSeconds + stats.narrowSeconds)}; память движка до ${formatBytes(stats.peakWorkingSetBytes)}` : null]
      .filter(Boolean).join(' · ')
    : '';

  return (
    <Stack gap="md">
      <Group gap="sm">
        {!readOnly && (
          <Button loading={start.isPending} disabled={active}
            onClick={() => (dirty && onSave
              ? askConfirm('Черновик не сохранён. Сохранить и прогнать?', async () => { await onSave(); start.mutate(); }, 'blue')
              : start.mutate())}>
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
        {queueText(current) && <Text size="sm" c="dimmed">{queueText(current)}</Text>}
        {current && (
          <Tooltip multiline w={360} withArrow disabled={!details} label={details}>
            <Text size="xs" c="dimmed">запущен {formatDateTime(current.createdAt)}</Text>
          </Tooltip>
        )}
        {!readOnly && <Button ml="auto" variant="default" onClick={() => setApproving(true)}>Утвердить…</Button>}
      </Group>
      {dirty && !readOnly && <Text size="sm" c="orange">Черновик не сохранён: прогон возьмёт сохранённую версию.</Text>}
      <EngineSilentAlert minutes={silent} />
      {current?.state === 'Failed' && current.error && <Alert color="red" title="Прогон не удался">{current.error}</Alert>}
      {approving && (
        <ApproveDialog partId={partId} nextVersion={(versions.data?.[0]?.version ?? 0) + 1} dirty={dirty} onSave={onSave} onClose={() => setApproving(false)} />
      )}
    </Stack>
  );
}
