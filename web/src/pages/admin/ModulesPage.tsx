import { Badge, Table, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ServerStats } from '../../api/types';
import { Panel } from '../../components/ui';
import { useModules } from '../../spaces/useSpaces';

/**
 * Модули сервера: какие включены и как их рабочие процессы. Только просмотр — набор модулей задаётся при установке строкой
 * MODULES в .env, переключателей здесь нет.
 */
export function ModulesPage() {
  const modules = useModules();
  const server = useApi<ServerStats>(['server'], '/api/server');
  const workers = server.data?.workers ?? [];

  return (
    <Panel title="Модули">
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Модуль</Table.Th>
            <Table.Th>Состояние</Table.Th>
            <Table.Th>Рабочий процесс</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {modules.map((module) => {
            const own = workers.filter((w) => w.module === module.name);
            return (
              <Table.Tr key={module.name}>
                <Table.Td fw={600}>{module.title}</Table.Td>
                <Table.Td>
                  <Badge color={module.enabled ? 'green' : 'gray'} variant="light">{module.enabled ? 'включён' : 'выключен'}</Badge>
                </Table.Td>
                <Table.Td>
                  {own.length === 0 ? <Text size="sm" c="dimmed">—</Text> : own.map((w) => (
                    <Text key={w.title} size="sm" c={w.online ? undefined : 'red'}>{w.title}: {w.online ? 'в сети' : 'не на связи'}</Text>
                  ))}
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
      <Text size="sm" c="dimmed" mt="md">
        {'Набор модулей задаётся строкой MODULES в файле .env на сервере. После изменения перезапустите: docker compose up -d. '}
        Выключенный модуль ничего не удаляет: включите его обратно — данные на месте.
      </Text>
    </Panel>
  );
}
