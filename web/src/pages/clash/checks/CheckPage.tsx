import { Link as RouterLink, useParams } from 'react-router';
import { Anchor, Badge, Breadcrumbs, Button, Group, Stack, Text, Title } from '@mantine/core';
import { useApi } from '../../../api/queries';
import { QueryError } from '../../../components/QueryState';
import type { CheckView } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { Panel } from '../../../components/ui';
import { ApprovedTab } from '../approved/ApprovedTab';
import { ScheduleLine } from '../part/ScheduleLine';

/** Страница утверждённой проверки: список коллизий, карточка, 3D, лента. Координатору — ссылка «Настроить» и прогоны. */
export function CheckPage() {
  const { id } = useParams();
  const partId = id!;
  const { can, canWork } = useAuth();
  const check = useApi<CheckView>(['clash-checks', partId], `/api/clash/checks/${partId}`);

  return (
    <Stack gap="sm">
      <Breadcrumbs>
        <Anchor component={RouterLink} to="/clash/checks" size="sm">Проверки</Anchor>
        <Text size="sm" c="dimmed">{check.data?.object ?? 'Без проекта'}</Text>
        <Text size="sm">{check.data?.name ?? ''}</Text>
      </Breadcrumbs>
      <Group justify="space-between" wrap="wrap">
        <Group gap="md">
          <Title order={2}>{check.data?.name ?? ''}</Title>
          {check.data && <Badge variant="light" color="green">утверждённая v{check.data.version}</Badge>}
        </Group>
        {can('Operator') && (
          <Button component={RouterLink} to={`/clash/setup/parts/${partId}`} size="xs" variant="default">Настроить</Button>
        )}
      </Group>
      <QueryError query={check} title="Проверка не открылась" />
      {check.data && <ScheduleLine partId={partId} approved canEdit={can('Operator')} />}
      <Panel>
        <ApprovedTab partId={partId} canEdit={canWork} canRun={can('Operator')} />
      </Panel>
    </Stack>
  );
}
