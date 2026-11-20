import { Link } from 'react-router';
import { Anchor, Badge, Box, Button, Group, Paper, Stack, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { AgentView, GeneralSettings } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { ConfirmButton, Empty, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatRelative } from '../../lib/format';
import { describeInstance, toneColor } from './agentStatus';

const stateColor = { Online: 'green', Draining: 'yellow', Offline: 'red', Disabled: 'gray' } as const;

/** Агенты и экземпляры Revit: что делает каждый, с кнопками «Отменить» и «Убить» для координатора. */
export function AgentsPanel({ agents, silence }: { agents: AgentView[]; silence: number }) {
  const kill = useApiMutation<{ agentId: string; slot: number }>('POST', ({ agentId, slot }) => `/api/agents/${agentId}/instances/${slot}/kill`, {
    invalidate: ['agents'],
    success: 'Команда отправлена агенту',
  });
  const cancel = useApiMutation<string>('POST', (jobId) => `/api/jobs/${jobId}/cancel`, { invalidate: ['agents', 'jobs'], success: 'Отмена отправлена' });

  if (agents.length === 0) {
    return (
      <Empty>
        Агенты не подключены — <Anchor component={Link} to="/farm/agents">Ферма → Агенты</Anchor>
      </Empty>
    );
  }

  return (
    <Stack gap="sm">
      {agents.map((agent) => (
        <Paper key={agent.id} withBorder p="sm" radius="sm">
          <Group justify="space-between" wrap="wrap" gap="xs" mb={agent.instances.length ? 6 : 0}>
            <Group gap={8}>
              <Box w={8} h={8} style={{ borderRadius: '50%', background: `var(--mantine-color-${stateColor[agent.state]}-6)` }} />
              <Text fw={600}>{agent.machineName}</Text>
              {agent.isDraining && <Badge color="yellow" variant="light">не берёт новые</Badge>}
            </Group>
            <Text size="xs" c={agent.state === 'Offline' ? 'red' : 'dimmed'}>
              {agent.state === 'Offline' || agent.state === 'Disabled'
                ? `${t.agentState[agent.state]} ${formatRelative(agent.lastSeenAt)}`
                : `пинг ${formatRelative(agent.lastSeenAt)} · CPU ${Math.round(agent.cpuPercent)}% · RAM ${Math.round(agent.ramUsedMb / 1024)}/${Math.round(agent.totalRamMb / 1024)} ГБ`}
            </Text>
          </Group>
          {agent.instances.map((instance) => {
            const status = describeInstance(instance, silence);
            return (
              <Group key={instance.slot} gap={8} wrap="nowrap" px="xs" py={6} bg="var(--mantine-color-default-hover)" style={{ borderRadius: 4 }} mt={4}>
                <YearBadge year={instance.year} />
                <Text size="sm" c="dimmed">
                  #{instance.slot}
                </Text>
                <Box w={8} h={8} style={{ flexShrink: 0, borderRadius: '50%', background: `var(--mantine-color-${toneColor[status.tone]}-6)` }} />
                <Text size="sm" c={status.tone === 'warn' ? 'orange' : undefined} style={{ flex: 1, minWidth: 0 }} truncate>
                  {status.text}
                </Text>
                <RequireRole role="Operator">
                  {instance.jobId && (
                    <Button size="compact-xs" variant="default" onClick={() => cancel.mutate(instance.jobId!)}>
                      Отменить
                    </Button>
                  )}
                  <ConfirmButton
                    size="compact-xs"
                    variant="default"
                    question={`Убить Revit #${instance.slot} на ${agent.machineName}? Текущая модель будет выгружена заново, если остались попытки.`}
                    onConfirm={() => kill.mutate({ agentId: agent.id, slot: instance.slot })}
                  >
                    Убить
                  </ConfirmButton>
                </RequireRole>
              </Group>
            );
          })}
        </Paper>
      ))}
    </Stack>
  );
}

/** Панель агентов со своими данными: агенты обновляются раз в 15 с. */
export function FarmAgents() {
  const agents = useApi<AgentView[]>(['agents'], '/api/agents', { refetchInterval: 15_000 });
  const settings = useApi<GeneralSettings>(['settings'], '/api/settings');
  return <AgentsPanel agents={agents.data ?? []} silence={settings.data?.silenceTimeoutMinutes ?? 10} />;
}
