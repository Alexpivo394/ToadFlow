import { Fragment } from 'react';
import { Link } from 'react-router';
import { Anchor, Group, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { TriggeredRunView } from '../../api/types';

const stateText: Record<TriggeredRunView['state'], string> = {
  Queued: 'в очереди', Running: 'идёт', Succeeded: 'готово', Failed: 'не удался', Cancelled: 'отменён',
};

/**
 * Карточка запуска выгрузки для коллизий: какие прогоны частей он поставил и чем они кончились — цепочка «выгрузка → прогон»
 * видна в «Истории», а не только в проверке. Строка ведёт в проверку части.
 */
export function ClashRunsLine({ runId }: { runId: string }) {
  const runs = useApi<TriggeredRunView[]>(['clash-collisions', 'triggered', runId], `/api/clash/extract-runs/${runId}/engine-runs`);
  if (!runs.data || runs.data.length === 0) return null;
  return (
    <Group gap={6} mb="sm" wrap="wrap" data-testid="clash-runs">
      <Text size="sm" fw={500}>Прогоны:</Text>
      {runs.data.map((r, i) => (
        <Fragment key={r.runId}>
          {i > 0 && <Text size="sm" c="dimmed">·</Text>}
          <Anchor component={Link} to={`/clash/checks/${r.partId}`} size="sm">{r.partName}</Anchor>
          <Text size="sm" c={r.state === 'Failed' ? 'red' : 'dimmed'} title={r.error ?? undefined}>
            — {stateText[r.state]}{r.new != null ? ` (новых ${r.new.toLocaleString('ru-RU')})` : ''}
          </Text>
        </Fragment>
      ))}
    </Group>
  );
}
