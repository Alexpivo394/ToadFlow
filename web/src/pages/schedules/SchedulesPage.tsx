import { Link, useNavigate, useParams } from 'react-router';
import { Badge, Box, Button, Stack, Table, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ScheduleView } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { Empty, PageTitle, Panel, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatDateTime, pluralRu } from '../../lib/format';
import { describeRecurrence } from './recurrenceText';
import { ScheduleEditor } from './ScheduleEditor';

function LastRun({ schedule }: { schedule: ScheduleView }) {
  const last = schedule.lastRun;
  if (!last) return <Text c="dimmed">—</Text>;
  const ok = last.failed === 0;
  return (
    <Text size="sm" c={ok ? 'green' : 'red'}>
      {ok ? '✓' : '✗'} {formatDateTime(last.createdAt)} {last.total - last.failed}/{last.total}
    </Text>
  );
}

export function SchedulesPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const schedules = useApi<ScheduleView[]>(['schedules'], '/api/schedules');
  const selected = id && id !== 'new' ? schedules.data?.find((s) => s.id === id) : undefined;

  return (
    <>
      <PageTitle
        actions={
          <RequireRole role="Operator">
            <Button component={Link} to="/schedules/new">
              + Новое расписание
            </Button>
          </RequireRole>
        }
      >
        {t.nav.schedules}
      </PageTitle>
      <Stack>
        <Panel>
          {schedules.data && schedules.data.length === 0 ? (
            <Empty>Расписаний пока нет</Empty>
          ) : (
            <Table.ScrollContainer minWidth={900}>
              <Table highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Название</Table.Th>
                    <Table.Th>Источник</Table.Th>
                    <Table.Th>Что</Table.Th>
                    <Table.Th>Когда</Table.Th>
                    <Table.Th>Следующий</Table.Th>
                    <Table.Th>Последний</Table.Th>
                    <Table.Th>Автор / изменил</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {schedules.data?.map((s) => (
                    <Table.Tr key={s.id} onClick={() => navigate(`/schedules/${s.id}`)} style={{ cursor: 'pointer', opacity: s.isEnabled ? 1 : 0.55 }}
                      bg={s.id === id ? 'var(--mantine-color-default-hover)' : undefined}>
                      <Table.Td fw={600}>{s.name}</Table.Td>
                      <Table.Td>
                        {/* Метки фиксированной ширины — названия источников и счётчики моделей начинаются с одной линии */}
                        <Box style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                          <Box miw={64} style={{ flexShrink: 0 }}>
                            {s.sourceRevitYear ? <YearBadge year={s.sourceRevitYear} muted={!s.isEnabled} /> : <Badge variant="light" color="gray" radius="sm">папка</Badge>}
                          </Box>
                          {s.sourceName}
                        </Box>
                      </Table.Td>
                      <Table.Td>
                        <Box style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                          <Box miw={92} style={{ flexShrink: 0 }}>
                            {s.moduleKind === 'loi' ? <Badge color="grape" variant="light">LOI</Badge> : <Badge color="blue" variant="light">RVT / NWC</Badge>}
                          </Box>
                          <span>
                            {s.modelCount} {pluralRu(s.modelCount, ['модель', 'модели', 'моделей'])}
                            {s.selection.folders.length > 0 && <Text span c="dimmed"> + {s.selection.folders.length} папк.</Text>}
                          </span>
                        </Box>
                      </Table.Td>
                      <Table.Td>{describeRecurrence(s)}</Table.Td>
                      <Table.Td>{s.isEnabled ? formatDateTime(s.nextRunAt) : <Badge color="gray" variant="light">выключено</Badge>}</Table.Td>
                      <Table.Td><LastRun schedule={s} /></Table.Td>
                      <Table.Td>
                        <Text size="sm">{s.createdBy}</Text>
                        {s.updatedBy !== s.createdBy && <Text size="xs" c="dimmed">изм. {s.updatedBy}, {formatDateTime(s.updatedAt)}</Text>}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Panel>
        {id === 'new' && <ScheduleEditor key="new" schedule={null} />}
        {selected && <ScheduleEditor key={selected.id + selected.updatedAt} schedule={selected} />}
      </Stack>
    </>
  );
}
