import { Link } from 'react-router';
import { Anchor, Badge, Box, Button, Group, Paper, SimpleGrid, Stack, Table, Text, Tooltip } from '@mantine/core';
import { BarChart } from '@mantine/charts';
import { useApi, useApiMutation } from '../../api/queries';
import type { AgentView, Dashboard, GeneralSettings } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { ConfirmButton, Empty, PageTitle, Panel, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatDateTime, formatDuration, formatRelative, pluralRu } from '../../lib/format';
import { describeInstance, toneColor } from './agentStatus';

function Kpi({ title, value, hint, color }: { title: string; value: string | number; hint: string; color?: string }) {
  return (
    <Paper withBorder p="md" radius="md">
      <Text size="xs" fw={700} c="dimmed" tt="uppercase">
        {title}
      </Text>
      <Text fz={28} fw={700} c={color}>
        {value}
      </Text>
      <Text size="xs" c="dimmed">
        {hint}
      </Text>
    </Paper>
  );
}

const stateColor = { Online: 'green', Draining: 'yellow', Offline: 'red', Disabled: 'gray' } as const;

function AgentsPanel({ agents, silence }: { agents: AgentView[]; silence: number }) {
  const kill = useApiMutation<{ agentId: string; slot: number }>('POST', ({ agentId, slot }) => `/api/agents/${agentId}/instances/${slot}/kill`, {
    invalidate: ['agents'],
    success: 'Команда отправлена агенту',
  });
  const cancel = useApiMutation<string>('POST', (jobId) => `/api/jobs/${jobId}/cancel`, { invalidate: ['agents', 'jobs'], success: 'Отмена отправлена' });

  if (agents.length === 0) {
    return (
      <Empty>
        Агенты не подключены — <Anchor component={Link} to="/settings/agents">Настройки → Агенты</Anchor>
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

export function DashboardPage() {
  const dashboard = useApi<Dashboard>(['dashboard'], '/api/dashboard', { refetchInterval: 60_000 });
  const agents = useApi<AgentView[]>(['agents'], '/api/agents', { refetchInterval: 15_000 });
  const settings = useApi<GeneralSettings>(['settings'], '/api/settings');
  const data = dashboard.data;
  const kpi = data?.kpi;

  return (
    <>
      <PageTitle>{t.nav.dashboard}</PageTitle>
      <SimpleGrid cols={{ base: 2, sm: 3, lg: 5 }} mb="md">
        <Kpi title="За 24 часа" value={kpi?.exported24h ?? '—'} hint={`${pluralRu(kpi?.exported24h ?? 0, ['модель обработана', 'модели обработано', 'моделей обработано'])}`} />
        <Kpi title="Ошибки" value={kpi?.failed24h ?? '—'} color={kpi?.failed24h ? 'red' : undefined} hint={`за 24 часа · ${kpi?.failedPercent ?? 0}%`} />
        <Kpi title="Очередь" value={kpi?.queued ?? '—'} hint={`ждут · ${kpi?.running ?? 0} в работе`} />
        <Kpi title="Среднее время" value={formatDuration(kpi?.avgDurationSeconds24h)} hint="на модель" />
        <Kpi
          title="Агенты"
          value={kpi ? `${kpi.agentsOnline} / ${kpi.agentsTotal}` : '—'}
          color={kpi && kpi.agentsOnline < kpi.agentsTotal ? 'orange' : 'green'}
          hint={kpi && kpi.agentsTotal > kpi.agentsOnline ? `в сети · ${kpi.agentsTotal - kpi.agentsOnline} недоступен` : 'в сети'}
        />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, md: 2 }} mb="md">
        <Panel title="Агенты и экземпляры Revit">
          <AgentsPanel agents={agents.data ?? []} silence={settings.data?.silenceTimeoutMinutes ?? 10} />
        </Panel>
        <Stack>
          <Panel title="Модели по дням (14 дней)">
            <BarChart
              h={160}
              data={(data?.daily ?? []).map((d) => ({ date: d.date.slice(8, 10) + '.' + d.date.slice(5, 7), Успешно: d.succeeded, 'С ошибкой': d.failed }))}
              dataKey="date"
              type="stacked"
              series={[
                { name: 'Успешно', color: 'green.6' },
                { name: 'С ошибкой', color: 'red.6' },
              ]}
              withLegend
              tickLine="none"
              gridAxis="none"
            />
          </Panel>
          <Panel title="Ближайшие запуски">
            {data && data.upcoming.length === 0 ? (
              <Empty>Расписаний нет — <Anchor component={Link} to="/schedules">создайте первое</Anchor></Empty>
            ) : (
              <Table>
                <Table.Tbody>
                  {data?.upcoming.map((u) => (
                    <Table.Tr key={u.scheduleId}>
                      <Table.Td>{formatDateTime(u.at)}</Table.Td>
                      <Table.Td>
                        <Anchor component={Link} to={`/schedules/${u.scheduleId}`}>
                          {u.name}
                        </Anchor>
                      </Table.Td>
                      <Table.Td c="dimmed">{u.modelCount} шт.</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Panel>
        </Stack>
      </SimpleGrid>

      <Panel title="Последние ошибки">
        {data && data.recentErrors.length === 0 ? (
          <Empty>Ошибок нет</Empty>
        ) : (
          <Table.ScrollContainer minWidth={600}>
            <Table>
              <Table.Tbody>
                {data?.recentErrors.map((e) => (
                  <Table.Tr key={e.jobId}>
                    <Table.Td c="dimmed">{formatDateTime(e.at)}</Table.Td>
                    <Table.Td>
                      <Anchor component={Link} to={`/history/runs/${e.runId}?job=${e.jobId}`}>
                        {e.modelName}
                      </Anchor>
                    </Table.Td>
                    <Table.Td c="red">
                      <Tooltip label={e.note} disabled={!e.note} multiline maw={400}>
                        <Text size="sm" lineClamp={1}>
                          {e.note}
                        </Text>
                      </Tooltip>
                    </Table.Td>
                    <Table.Td c="dimmed">
                      {[e.agentName, e.revitYear ? `R${e.revitYear}` : null].filter(Boolean).join(' · ')}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Panel>
    </>
  );
}
