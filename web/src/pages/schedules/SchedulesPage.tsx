import { Link, useNavigate, useParams } from 'react-router';
import { Badge, Box, Button, Stack, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ScheduleView } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { DataTable } from '../../components/DataTable';
import { PageTitle, Panel, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatDateTime, pluralRu } from '../../lib/format';
import { describeRecurrence } from './recurrenceText';
import { ScheduleEditor } from './ScheduleEditor';
import type { ModuleName } from '../../spaces/modules';

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

/**
 * Расписания модуля: только его записи, ссылки — внутри пространства модуля (<c>base</c>).
 */
export function SchedulesPage({ module, base }: { module: ModuleName; base: string }) {
  const { id } = useParams();
  const navigate = useNavigate();
  const schedules = useApi<ScheduleView[]>(['schedules', module], `/api/schedules?module=${module}`);
  const selected = id && id !== 'new' ? schedules.data?.find((s) => s.id === id) : undefined;

  return (
    <>
      <PageTitle
        actions={
          <RequireRole role="Operator">
            <Button component={Link} to={`${base}/new`}>
              + Новое расписание
            </Button>
          </RequireRole>
        }
      >
        {t.nav.schedules}
      </PageTitle>
      <Stack>
        <Panel>
          <DataTable rows={schedules.data ?? []} rowKey={(s) => s.id} search={(s) => `${s.name} ${s.sourceName}`} searchPlaceholder="название, источник…"
            onRowClick={(s) => navigate(`${base}/${s.id}`)} empty="Расписаний пока нет"
            rowStyle={(s) => ({ opacity: s.isEnabled ? 1 : 0.55, background: s.id === id ? 'var(--mantine-color-default-hover)' : undefined })}
            columns={[
              { key: 'name', title: 'Название', sort: (s) => s.name, render: (s) => <Text fw={600} size="sm">{s.name}</Text> },
              {
                key: 'source', title: 'Источник', sort: (s) => s.sourceName,
                render: (s) => (
                  // Метки фиксированной ширины — названия источников начинаются с одной линии
                  <Box style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <Box miw={64} style={{ flexShrink: 0 }}>
                      {s.sourceRevitYear ? <YearBadge year={s.sourceRevitYear} muted={!s.isEnabled} /> : <Badge variant="light" color="gray" radius="sm">папка</Badge>}
                    </Box>
                    {s.sourceName}
                  </Box>
                ),
              },
              {
                key: 'what', title: 'Что', sort: (s) => s.modelCount,
                render: (s) => (
                  <Box style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <Box miw={92} style={{ flexShrink: 0 }}>
                      {s.moduleKind === 'loi' ? (
                        <Badge color="grape" variant="light">LOI</Badge>
                      ) : s.moduleKind === 'clash-extract' ? (
                        <Badge color="orange" variant="light">Коллизии</Badge>
                      ) : (
                        <Badge color="blue" variant="light">RVT / NWC</Badge>
                      )}
                    </Box>
                    <span>
                      {s.modelCount} {pluralRu(s.modelCount, ['модель', 'модели', 'моделей'])}
                      {s.selection.folders.length > 0 && <Text span c="dimmed"> + {s.selection.folders.length} папк.</Text>}
                    </span>
                  </Box>
                ),
              },
              { key: 'when', title: 'Когда', render: (s) => describeRecurrence(s) },
              {
                key: 'next', title: 'Следующий', sort: (s) => (s.isEnabled ? s.nextRunAt : null),
                render: (s) => (s.isEnabled ? formatDateTime(s.nextRunAt) : <Badge color="gray" variant="light">выключено</Badge>),
                filter: { options: [{ value: 'on', label: 'включено' }, { value: 'off', label: 'выключено' }], match: (s, v) => v.includes(s.isEnabled ? 'on' : 'off') },
              },
              { key: 'last', title: 'Последний', sort: (s) => s.lastRun?.createdAt ?? null, render: (s) => <LastRun schedule={s} /> },
              {
                key: 'author', title: 'Автор / изменил', sort: (s) => s.updatedAt,
                render: (s) => (
                  <>
                    <Text size="sm">{s.createdBy}</Text>
                    {s.updatedBy !== s.createdBy && <Text size="xs" c="dimmed">изм. {s.updatedBy}, {formatDateTime(s.updatedAt)}</Text>}
                  </>
                ),
              },
            ]} />
        </Panel>
        {id === 'new' && <ScheduleEditor key="new" schedule={null} module={module} base={base} />}
        {selected && <ScheduleEditor key={selected.id + selected.updatedAt} schedule={selected} module={module} base={base} />}
      </Stack>
    </>
  );
}
