import { useState } from 'react';
import { useNavigate } from 'react-router';
import { Badge, Card, Group, SimpleGrid, Stack, Text, UnstyledButton } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ClashPartSummary } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { Empty } from '../../../components/ui';
import { formatDateTime } from '../../../lib/format';
import { NewPartWizard } from './NewPartWizard';
import type { ProjectKey } from './ProjectList';

function RunLine({ part }: { part: ClashPartSummary }) {
  const run = part.lastRun;
  if (!run) return <Text size="xs" c="dimmed">не прогонялся</Text>;
  if (run.state === 'Succeeded')
    return <Text size="xs" c="dimmed">прогон {formatDateTime(run.finishedAt ?? run.createdAt)} · {(run.hitCount ?? 0).toLocaleString('ru-RU')} коллизий</Text>;
  if (run.state === 'Failed') return <Text size="xs" c="red">прогон не удался</Text>;
  return <Text size="xs" c="blue">прогон {run.state === 'Queued' ? 'в очереди' : 'идёт'}</Text>;
}

/** Утверждённая версия и итог её последнего прогона: открытых после прогона — новые, активные и вернувшиеся. */
function ApprovedLine({ part }: { part: ClashPartSummary }) {
  if (part.approvedVersion == null) return <Text size="xs" c="dimmed">не утверждена</Text>;
  const last = part.lastApproved;
  return (
    <Text size="xs" c="green">
      утверждена v{part.approvedVersion}
      {last ? ` · открытых ${(last.new + last.active + last.returned).toLocaleString('ru-RU')}, исправлено ${last.fixed.toLocaleString('ru-RU')}` : ' · не прогонялась'}
    </Text>
  );
}

/** Карточки частей проекта и «+ Новая часть». */
export function PartCards({ project }: { project: ProjectKey }) {
  const { can } = useAuth();
  const navigate = useNavigate();
  const [creating, setCreating] = useState(false);
  const parts = useApi<ClashPartSummary[]>(['clash-parts', 'list', project], `/api/clash/parts?object=${encodeURIComponent(project)}`);

  return (
    <Stack gap="md" style={{ flex: 1, minWidth: 0 }}>
      <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }} spacing="md">
        {(parts.data ?? []).map((p) => (
          <UnstyledButton key={p.id} onClick={() => navigate(`/clash/parts/${p.id}`)}>
            <Card withBorder radius="md" padding="md" h="100%">
              <Text fw={600} size="lg">{p.name}</Text>
              <Group gap={4} my="xs">
                {p.groups.map((g) => <Badge key={g} variant="light" color="gray" size="sm">{g}</Badge>)}
                {p.warnings.map((w) => <Badge key={w} variant="light" color="orange" size="sm">{w}</Badge>)}
              </Group>
              <Text size="xs" c="dimmed">{p.models} моделей · {p.sets} наборов</Text>
              <RunLine part={p} />
              <ApprovedLine part={p} />
            </Card>
          </UnstyledButton>
        ))}
        {can('Operator') && (
          <UnstyledButton onClick={() => setCreating(true)} aria-label="+ Новая часть">
            <Card withBorder radius="md" padding="md" h="100%" mih={120} style={{ borderStyle: 'dashed', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <Text c="dimmed">+ Новая часть</Text>
            </Card>
          </UnstyledButton>
        )}
      </SimpleGrid>
      {parts.data && parts.data.length === 0 && !can('Operator') && <Empty>В этом проекте частей ещё нет</Empty>}
      {creating && <NewPartWizard project={project} onClose={() => setCreating(false)} />}
    </Stack>
  );
}
