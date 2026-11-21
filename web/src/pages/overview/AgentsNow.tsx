import { Link } from 'react-router';
import { Anchor, Box, Group, SimpleGrid, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { AgentView, InstanceStatus } from '../../api/types';
import { YearBadge } from '../../components/ui';
import { formatRelative } from '../../lib/format';

/** «идёт 12 мин», «идёт 1 ч 5 мин». */
function running(since: string | null, now: Date): string {
  if (!since) return 'идёт';
  const minutes = Math.max(0, Math.floor((now.getTime() - new Date(since).getTime()) / 60_000));
  return minutes < 60 ? `идёт ${minutes} мин` : `идёт ${Math.floor(minutes / 60)} ч ${minutes % 60} мин`;
}

/** Строка экземпляра Revit: модель, этап и сколько идёт; хода работы сервер не знает — полоска бежит без процентов. */
function Instance({ instance, now }: { instance: InstanceStatus; now: Date }) {
  const busy = instance.state === 'Busy' || instance.state === 'Unresponsive';
  return (
    <Box mt={10}>
      <Group gap={6} wrap="nowrap">
        <YearBadge year={instance.year} />
        {busy ? (
          <>
            <Text size="sm" fw={500} truncate style={{ flex: 1, minWidth: 0 }}>{instance.modelName ?? 'модель'}</Text>
            <Text size="xs" c={instance.state === 'Unresponsive' ? 'orange' : 'dimmed'} style={{ flexShrink: 0 }}>
              {instance.state === 'Unresponsive' ? 'не отвечает' : [instance.stage, running(instance.stageSince, now)].filter(Boolean).join(' · ')}
            </Text>
          </>
        ) : (
          <Text size="sm" c="dimmed">
            {instance.state === 'Starting' ? 'запускается' : instance.state === 'Restarting' ? 'перезапускается' : 'свободен'}
          </Text>
        )}
      </Group>
      {busy && <div className="ov-run"><div /></div>}
    </Box>
  );
}

/** Машина: точка связи, загрузка и что делает каждый её Revit. Кнопки управления — в разделе агентов, здесь только смотреть. */
function Machine({ agent, now }: { agent: AgentView; now: Date }) {
  const online = agent.state === 'Online' || agent.state === 'Draining';
  return (
    <div className="ov-machine">
      <Group gap={8} wrap="nowrap">
        <span className="ov-dot" data-tone={online ? 'live' : agent.state === 'Disabled' ? 'idle' : 'bad'} />
        <Text fw={650} truncate>{agent.machineName}</Text>
        <Text size="xs" c={online ? 'dimmed' : 'red'} ml="auto" style={{ flexShrink: 0 }}>
          {online
            ? `CPU ${Math.round(agent.cpuPercent)} % · RAM ${Math.round(agent.ramUsedMb / 1024)}/${Math.round(agent.totalRamMb / 1024)} ГБ`
            : agent.state === 'Disabled' ? 'отключён' : `не в сети ${formatRelative(agent.lastSeenAt, now).replace(' назад', '')}`}
        </Text>
      </Group>
      {online && agent.instances.map((instance) => <Instance key={instance.slot} instance={instance} now={now} />)}
    </div>
  );
}

/** «Агенты прямо сейчас»: карточка на машину. Агенты обновляются раз в 15 с и по живым событиям. */
export function AgentsNow() {
  const agents = useApi<AgentView[]>(['agents'], '/api/agents', { refetchInterval: 15_000 });
  const now = new Date();
  const list = agents.data ?? [];
  return (
    <section aria-label="Агенты прямо сейчас">
      <Text fw={650} mb="xs">Агенты прямо сейчас</Text>
      {agents.data && list.length === 0 ? (
        <Text c="dimmed" size="sm">
          Агенты не подключены — <Anchor component={Link} to="/farm/agents">подключить</Anchor>
        </Text>
      ) : (
        <SimpleGrid cols={{ base: 1, sm: 2, xl: 3 }} spacing="sm">
          {list.map((agent) => <Machine key={agent.id} agent={agent} now={now} />)}
        </SimpleGrid>
      )}
    </section>
  );
}
