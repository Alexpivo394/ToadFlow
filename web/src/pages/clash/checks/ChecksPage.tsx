import { Link as RouterLink, useSearchParams } from 'react-router';
import { Anchor, Badge, Button, Group, Stack, Table, Text, Title } from '@mantine/core';
import { useApi } from '../../../api/queries';
import { QueryError } from '../../../components/QueryState';
import type { CheckView } from '../../../api/types';
import { Empty, PageTitle, Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime } from '../../../lib/format';

const noProject = 'Без проекта';

/**
 * «Коллизии → Проверки»: утверждённые проверки по проектам; строка ведёт на страницу проверки. Фильтры <c>object</c>,
 * <c>discipline</c>, <c>pair</c> и <c>stuckDays</c> в адресе — из «Сводки»: проверки проекта, с моделями раздела или пары;
 * раздел, пара и «висят» переходят в список коллизий проверки.
 */
export function ChecksPage() {
  const [params, setParams] = useSearchParams();
  const object = params.get('object');
  const discipline = params.get('discipline');
  const pair = params.get('pair');
  const stuckDays = params.get('stuckDays');
  const checks = useApi<CheckView[]>(['clash-checks'], '/api/clash/checks');
  const has = (c: CheckView, d: string) => c.models.some((m) => m.discipline === d);
  const shown = (checks.data ?? []).filter((c) => (!object || (c.object ?? noProject) === object)
    && (!discipline || has(c, discipline)) && (!pair || pair.split(' × ').every((d) => has(c, d))));
  const listQuery = new URLSearchParams();
  if (discipline) listQuery.set('discipline', discipline);
  if (pair) listQuery.set('pair', pair);
  if (stuckDays) listQuery.set('stuckDays', stuckDays);
  const listTail = listQuery.toString() ? `?${listQuery}` : '';
  const byProject = new Map<string, CheckView[]>();
  for (const check of shown) {
    const key = check.object ?? noProject;
    byProject.set(key, [...(byProject.get(key) ?? []), check]);
  }

  return (
    <>
      <PageTitle>{t.nav.clashChecks}</PageTitle>
      {(object || discipline || pair || stuckDays) && (
        <Group gap="xs" mb="sm">
          {object && <Badge variant="light">Проект: {object}</Badge>}
          {discipline && <Badge variant="light">Раздел: {discipline}</Badge>}
          {pair && <Badge variant="light">Пара: {pair}</Badge>}
          {stuckDays && <Badge variant="light" color="orange">Висят дольше {stuckDays} дн.</Badge>}
          <Button size="compact-xs" variant="subtle" onClick={() => setParams({})}>все проверки</Button>
        </Group>
      )}
      <QueryError query={checks} title="Проверки не загрузились" />
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
                        <Anchor component={RouterLink} to={`/clash/checks/${check.partId}${listTail}`} fw={600}>{check.name}</Anchor>
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
