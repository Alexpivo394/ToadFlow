import { Link as RouterLink, useSearchParams } from 'react-router';
import { Anchor, Badge, Button, Group, Stack, Table, Text, Title } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { CheckView } from '../../../api/types';
import { Empty, PageTitle, Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime } from '../../../lib/format';

const noProject = 'Без проекта';

/**
 * «Коллизии → Проверки»: утверждённые проверки по проектам; строка ведёт на страницу проверки. Фильтры <c>object</c> и
 * <c>discipline</c> в адресе — из «Сводки»: проверки проекта или с моделями раздела.
 */
export function ChecksPage() {
  const [params, setParams] = useSearchParams();
  const object = params.get('object');
  const discipline = params.get('discipline');
  const checks = useApi<CheckView[]>(['clash-checks'], '/api/clash/checks');
  const shown = (checks.data ?? []).filter((c) => (!object || (c.object ?? noProject) === object)
    && (!discipline || c.models.some((m) => m.discipline === discipline)));
  const byProject = new Map<string, CheckView[]>();
  for (const check of shown) {
    const key = check.object ?? noProject;
    byProject.set(key, [...(byProject.get(key) ?? []), check]);
  }

  return (
    <>
      <PageTitle>{t.nav.clashChecks}</PageTitle>
      {(object || discipline) && (
        <Group gap="xs" mb="sm">
          {object && <Badge variant="light">Проект: {object}</Badge>}
          {discipline && <Badge variant="light">Раздел: {discipline}</Badge>}
          <Button size="compact-xs" variant="subtle" onClick={() => setParams({})}>все проверки</Button>
        </Group>
      )}
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
