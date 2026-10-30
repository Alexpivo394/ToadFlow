import type { ReactNode } from 'react';
import { Link as RouterLink } from 'react-router';
import { Anchor, Badge, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ClashOverview, ClashSettingsView, OverviewPart } from '../../../api/types';
import { Empty, PageTitle, Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { pluralRu } from '../../../lib/format';

/** Сколько всего проблем в обзоре. */
export function problemCount(o: ClashOverview): number {
  return o.notUploaded.length + o.runFailed.length + o.noRecentRun.length + o.unchecked.length + o.spike.length + o.stuck.length + o.pendingUsers;
}

export const pendingText = (n: number) => `${n} ${pluralRu(n, ['заявка', 'заявки', 'заявок'])} на регистрацию`;

function Card({ title, count, color = 'orange', children }: { title: string; count: number; color?: string; children: ReactNode }) {
  if (count === 0) return null;
  return (
    <Panel title={title} actions={<Badge color={color} variant="light">{count}</Badge>}>
      <Stack gap={4}>{children}</Stack>
    </Panel>
  );
}

function PartLinks({ parts, to }: { parts: OverviewPart[]; to: (p: OverviewPart) => string }) {
  return parts.map((p) => (
    <Anchor key={`${p.partId}-${p.reason}`} component={RouterLink} to={to(p)} size="sm">
      {p.name} — {p.reason}
    </Anchor>
  ));
}

/**
 * «Настройка проверок → Обзор»: первым делом утром — что не выгрузилось, какие прогоны не прошли, всплески новых,
 * висящие коллизии и заявки. Каждый пункт ведёт туда, где проблему решают.
 */
export function OverviewPage() {
  const overview = useApi<ClashOverview>(['clash-overview'], '/api/clash/overview', { refetchInterval: 60_000 });
  const settings = useApi<ClashSettingsView>(['clash-settings'], '/api/clash/settings');
  const o = overview.data;
  const stuckDays = settings.data?.overview.stuckDays ?? 30;

  return (
    <>
      <PageTitle>{t.clash.overviewTitle}</PageTitle>
      {o && problemCount(o) === 0 && <Empty>Всё в порядке</Empty>}
      {o && (
        <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md">
          <Card title="Не выгрузилось" count={o.notUploaded.length} color="red">
            {o.notUploaded.map((m) => (
              <Anchor key={`${m.partId}-${m.modelId}`} component={RouterLink} to={`/clash/setup/parts/${m.partId}?tab=models`} size="sm">
                {m.partName}: {m.modelName} — {m.reason}
              </Anchor>
            ))}
          </Card>
          <Card title="Прогон не прошёл" count={o.runFailed.length + o.noRecentRun.length} color="red">
            <PartLinks parts={o.runFailed} to={(p) => `/clash/checks/${p.partId}`} />
            <PartLinks parts={o.noRecentRun} to={(p) => `/clash/checks/${p.partId}`} />
          </Card>
          <Card title="Не проверены" count={o.unchecked.length}>
            <PartLinks parts={o.unchecked} to={(p) => `/clash/checks/${p.partId}`} />
          </Card>
          <Card title="Всплеск новых" count={o.spike.length}>
            <PartLinks parts={o.spike} to={(p) => `/clash/checks/${p.partId}`} />
          </Card>
          <Card title="Висят" count={o.stuck.length}>
            <PartLinks parts={o.stuck} to={(p) => `/clash/checks/${p.partId}?stuckDays=${stuckDays}`} />
          </Card>
          <Card title="Заявки на регистрацию" count={o.pendingUsers} color="blue">
            <Group>
              <Anchor component={RouterLink} to="/settings/users" size="sm">{pendingText(o.pendingUsers)}</Anchor>
              <Text size="xs" c="dimmed">подтвердить и указать разделы</Text>
            </Group>
          </Card>
        </SimpleGrid>
      )}
    </>
  );
}
