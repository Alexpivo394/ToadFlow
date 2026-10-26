import { useState } from 'react';
import { Alert, Anchor, Badge, Button, Code, Group, List, Menu, Modal, NumberInput, Stack, Table, Text } from '@mantine/core';
import { IconChevronDown } from '@tabler/icons-react';
import { useApi, useApiMutation } from '../../api/queries';
import type { AgentView, VersionLimit } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { askConfirm, CopyTextButton, Empty, Panel, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatDateTime, formatRelative } from '../../lib/format';
import { validateLimits } from './limits';

const stateColor = { Online: 'green', Draining: 'yellow', Offline: 'red', Disabled: 'gray' } as const;

function LimitsModal({ agent, onClose }: { agent: AgentView; onClose: () => void }) {
  const [limits, setLimits] = useState<VersionLimit[]>(() =>
    agent.revits.map((r) => ({ year: r.year, maxInstances: agent.limits.find((l) => l.year === r.year)?.maxInstances ?? 0 })),
  );
  const error = validateLimits(limits);
  const save = useApiMutation<VersionLimit[]>('PUT', () => `/api/agents/${agent.id}/limits`, { invalidate: ['agents'], success: 'Лимиты сохранены и отправлены агенту' });

  return (
    <Modal opened onClose={onClose} title={`Экземпляры Revit на ${agent.machineName}`}>
      <Stack>
        {limits.map((limit, i) => (
          <NumberInput
            key={limit.year}
            label={`Revit ${limit.year}${agent.revits.find((r) => r.year === limit.year)?.hasNwcExporter ? '' : ' (нет Navisworks Exporter — только RVT)'}`}
            min={0}
            allowDecimal={false}
            value={limit.maxInstances}
            onChange={(v) => setLimits(limits.map((l, j) => (j === i ? { ...l, maxInstances: Number(v) || 0 } : l)))}
          />
        ))}
        <Text size="sm" c="dimmed">
          Всего одновременно: {limits.reduce((sum, l) => sum + l.maxInstances, 0)}. Учитывайте память: тяжёлая модель может занимать 10–20 ГБ.
        </Text>
        {error && <Alert color="red">{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t.common.cancel}
          </Button>
          <Button disabled={error !== null} loading={save.isPending} onClick={() => save.mutate(limits, { onSuccess: onClose })}>
            {t.common.save}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function ConnectAgentModal({ onClose }: { onClose: () => void }) {
  const create = useApiMutation<void, { code: string; expiresAt: string }>('POST', () => '/api/agents/registration-codes');

  return (
    <Modal opened onClose={onClose} title="Подключение нового агента" size="lg">
      <List type="ordered" spacing="sm">
        <List.Item>Установите агента ToadFlow на Windows-машину с Revit (аддин ставится вместе с ним под все найденные версии). Установщик — в <Anchor href="https://github.com/Alexpivo394/ToadFlow/releases" target="_blank">релизах</Anchor>, инструкция — <Anchor href="https://github.com/Alexpivo394/ToadFlow/wiki/Agent" target="_blank">в вики</Anchor>.</List.Item>
        <List.Item>
          При установке укажите адрес этого сервера и одноразовый код:{' '}
          {create.data ? (
            <Group gap="xs" mt={6}>
              <Code fz="lg">{create.data.code}</Code>
              <CopyTextButton value={create.data.code} label="Копировать" copiedLabel="Скопировано" />
              <Text size="xs" c="dimmed">действует до {formatDateTime(create.data.expiresAt)}</Text>
            </Group>
          ) : (
            <Button size="compact-sm" mt={6} onClick={() => create.mutate()} loading={create.isPending}>
              Создать код
            </Button>
          )}
        </List.Item>
        <List.Item>Агент появится в списке и сам сообщит, какие Revit нашёл. Здесь задаётся, сколько экземпляров каждой версии он держит.</List.Item>
      </List>
    </Modal>
  );
}

export function AgentsSettings() {
  const agents = useApi<AgentView[]>(['agents'], '/api/agents');
  const [editing, setEditing] = useState<AgentView | null>(null);
  const [connecting, setConnecting] = useState(false);
  const drain = useApiMutation<{ id: string; draining: boolean }>('POST', ({ id }) => `/api/agents/${id}/drain`, {
    invalidate: ['agents'],
    json: ({ draining }) => ({ draining }),
  });
  const disable = useApiMutation<string>('POST', (id) => `/api/agents/${id}/disable`, { invalidate: ['agents'], success: 'Агент отключён' });
  const remove = useApiMutation<string>('DELETE', (id) => `/api/agents/${id}`, { invalidate: ['agents'], success: 'Агент удалён' });

  return (
    <Panel
      title="Агенты"
      actions={
        <RequireRole role="Admin">
          <Button size="xs" onClick={() => setConnecting(true)}>
            + Подключить агента
          </Button>
        </RequireRole>
      }
    >
      {agents.data && agents.data.length === 0 ? (
        <Empty>Агентов пока нет — нажмите «Подключить агента»</Empty>
      ) : (
        <Table.ScrollContainer minWidth={680}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Машина</Table.Th>
                <Table.Th>Статус</Table.Th>
                <Table.Th>Найденные Revit</Table.Th>
                <Table.Th>Экземпляров (макс.)</Table.Th>
                <Table.Th>Версия агента</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {agents.data?.map((agent) => (
                <Table.Tr key={agent.id}>
                  <Table.Td>
                    <Text fw={600}>{agent.machineName}</Text>
                    <Text size="xs" c="dimmed">{[agent.ip, agent.totalRamMb ? `${Math.round(agent.totalRamMb / 1024)} ГБ RAM` : null].filter(Boolean).join(' · ')}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Text c={stateColor[agent.state]} size="sm" style={{ whiteSpace: 'nowrap' }}>
                      {t.agentState[agent.state]}
                      {agent.state === 'Offline' && ` ${formatRelative(agent.lastSeenAt)}`}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Group gap={4}>
                      {agent.revits.map((r) => <YearBadge key={r.year} year={r.year} />)}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    {agent.limits.map((l) => `R${l.year}: ${l.maxInstances}`).join(' · ') || '—'}
                  </Table.Td>
                  <Table.Td>
                    {agent.agentVersion || '—'} {agent.isOutdated && <Badge color="orange" variant="light">устарел</Badge>}
                  </Table.Td>
                  <Table.Td>
                    <RequireRole role="Admin">
                      {/* Действия — меню: строка таблицы не расползается и ничего не обрезается на узком экране */}
                      <Group justify="flex-end">
                        <Menu position="bottom-end" withinPortal>
                          <Menu.Target>
                            <Button size="compact-sm" variant="default" rightSection={<IconChevronDown size={14} />}>Действия</Button>
                          </Menu.Target>
                          <Menu.Dropdown>
                            {agent.state !== 'Disabled' && (
                              <>
                                <Menu.Item onClick={() => setEditing(agent)} disabled={agent.revits.length === 0}>Лимиты экземпляров</Menu.Item>
                                <Menu.Item onClick={() => drain.mutate({ id: agent.id, draining: !agent.isDraining })}>
                                  {agent.isDraining ? 'Брать новые задания' : 'Не брать новые задания'}
                                </Menu.Item>
                                <Menu.Item onClick={() => askConfirm(`Отключить агента ${agent.machineName}? Он больше не сможет подключиться.`, () => disable.mutate(agent.id))}>
                                  Отключить
                                </Menu.Item>
                              </>
                            )}
                            {(agent.state === 'Offline' || agent.state === 'Disabled') && (
                              <Menu.Item color="red" onClick={() => askConfirm(`Удалить агента ${agent.machineName}?`, () => remove.mutate(agent.id))}>
                                {t.common.delete}
                              </Menu.Item>
                            )}
                          </Menu.Dropdown>
                        </Menu>
                      </Group>
                    </RequireRole>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
      {editing && <LimitsModal agent={editing} onClose={() => setEditing(null)} />}
      {connecting && <ConnectAgentModal onClose={() => setConnecting(false)} />}
    </Panel>
  );
}
