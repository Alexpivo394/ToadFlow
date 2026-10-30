import { Link as RouterLink } from 'react-router';
import { Alert, Anchor, Group, Text } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ClashOverview } from '../../../api/types';
import { pluralRu } from '../../../lib/format';
import { pendingText, problemCount } from './OverviewPage';

/** Блок «Коллизии: требует внимания» на общем «Обзоре» ToadFlow: числа по видам проблем, каждое ведёт в обзор проверок. */
export function ClashAttention() {
  const overview = useApi<ClashOverview>(['clash-overview'], '/api/clash/overview', { refetchInterval: 60_000 });
  const o = overview.data;
  if (!o) return null;
  if (problemCount(o) === 0) return <Alert color="green" mb="md" p="xs">Коллизии: всё в порядке</Alert>;

  const failed = o.runFailed.length + o.noRecentRun.length;
  const items = [
    o.notUploaded.length > 0 && `${o.notUploaded.length} ${pluralRu(o.notUploaded.length, ['модель не выгрузилась', 'модели не выгрузились', 'моделей не выгрузились'])}`,
    failed > 0 && `${failed} ${pluralRu(failed, ['прогон не прошёл', 'прогона не прошли', 'прогонов не прошли'])}`,
    o.unchecked.length > 0 && `${o.unchecked.length} ${pluralRu(o.unchecked.length, ['проверка с непроверенными', 'проверки с непроверенными', 'проверок с непроверенными'])}`,
    o.spike.length > 0 && `${o.spike.length} ${pluralRu(o.spike.length, ['всплеск новых', 'всплеска новых', 'всплесков новых'])}`,
    o.stuck.length > 0 && `${o.stuck.length} ${pluralRu(o.stuck.length, ['проверка с висящими', 'проверки с висящими', 'проверок с висящими'])}`,
    o.pendingUsers > 0 && pendingText(o.pendingUsers),
  ].filter((x): x is string => Boolean(x));

  return (
    <Alert color="orange" mb="md" p="xs" title="Коллизии: требует внимания">
      <Group gap="md">
        {items.map((text) => <Anchor key={text} component={RouterLink} to="/clash/setup" size="sm">{text}</Anchor>)}
        <Text size="xs" c="dimmed">подробно — в обзоре проверок</Text>
      </Group>
    </Alert>
  );
}
