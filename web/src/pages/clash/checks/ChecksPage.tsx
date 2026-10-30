import { Link as RouterLink } from 'react-router';
import { Anchor, Badge, Group, Stack, Table, Text, Title } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { CheckView } from '../../../api/types';
import { Empty, PageTitle, Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime } from '../../../lib/format';

const noProject = 'Без проекта';

/** «Коллизии → Проверки»: утверждённые проверки по проектам; строка ведёт на страницу проверки. */
export function ChecksPage() {
  const checks = useApi<CheckView[]>(['clash-checks'], '/api/clash/checks');
  const byProject = new Map<string, CheckView[]>();
  for (const check of checks.data ?? []) {
    const key = check.object ?? noProject;
    byProject.set(key, [...(byProject.get(key) ?? []), check]);
  }

  return (
    <>
      <PageTitle>{t.nav.clashChecks}</PageTitle>
      {checks.data && checks.data.length === 0 && <Empty>Утверждённых проверок пока нет — их утверждает координатор в «Настройке проверок»</Empty>}
      <Stack gap="md">
        {[...byProject].map(([project, list]) => (
          <Panel key={project}>
            <Title order={4} mb="xs">{project}</Title>
            <Table highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Проверка</Table.Th>
                  <Table.Th>Разделы</Table.Th>
                  <Table.Th ta="right">Открытых</Table.Th>
                  <Table.Th>Последний прогон</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {list.map((check) => (
                  <Table.Tr key={check.partId}>
                    <Table.Td>
                      <Group gap="xs">
                        <Anchor component={RouterLink} to={`/clash/checks/${check.partId}`} fw={600}>{check.name}</Anchor>
                        <Badge variant="light" color="green" size="sm">v{check.version}</Badge>
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{[...new Set(check.models.map((m) => m.discipline).filter(Boolean))].join(', ') || '—'}</Text>
                    </Table.Td>
                    <Table.Td ta="right">{check.open.toLocaleString('ru-RU')}</Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">{check.lastRunAt ? formatDateTime(check.lastRunAt) : 'не прогонялась'}</Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Panel>
        ))}
      </Stack>
    </>
  );
}
