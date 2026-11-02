import { Fragment } from 'react';
import { Link as RouterLink } from 'react-router';
import { Anchor, Button, Group, Menu, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../../api/queries';
import type { PartScheduleView } from '../../../api/types';
import { describeRecurrence } from '../../schedules/recurrenceText';

/**
 * Строка о регулярном прогоне части: какими расписаниями выгрузки она прогоняется (имя — ссылка на расписание), либо
 * «Регулярного прогона нет» с меню «Добавить в расписание» — выбор сохраняется сразу. До утверждения — подсказка.
 */
export function ScheduleLine({ partId, approved, canEdit }: { partId: string; approved: boolean; canEdit: boolean }) {
  const schedules = useApi<PartScheduleView[]>(['clash-parts', partId, 'schedules'], approved ? `/api/clash/parts/${partId}/schedules` : null);
  const save = useApiMutation<{ scheduleIds: string[] }, PartScheduleView[]>('PUT', () => `/api/clash/parts/${partId}/schedules`,
    { invalidate: ['clash-parts', 'schedules'] });

  if (!approved) return <Text size="sm" c="dimmed" data-testid="schedule-line">Регулярный прогон — после утверждения</Text>;
  if (!schedules.data) return null;

  const selected = schedules.data.filter((s) => s.selected);
  if (selected.length > 0)
    return (
      <Text size="sm" data-testid="schedule-line">
        Прогоняется расписанием{selected.length > 1 ? 'и' : ''}{' '}
        {selected.map((s, i) => (
          <Fragment key={s.id}>
            {i > 0 && ', '}
            «<Anchor component={RouterLink} to={`/schedules/${s.id}`} size="sm">{s.name}</Anchor>» — {describeRecurrence(s)}
            {s.isEnabled ? '' : ' (выключено)'}
          </Fragment>
        ))}
      </Text>
    );

  return (
    <Group gap="xs" data-testid="schedule-line">
      <Text size="sm" c="orange">Регулярного прогона нет</Text>
      {canEdit && schedules.data.length > 0 && (
        <Menu position="bottom-start" withinPortal>
          <Menu.Target>
            <Button size="compact-xs" variant="light" loading={save.isPending}>· Добавить в расписание ▾</Button>
          </Menu.Target>
          <Menu.Dropdown>
            {schedules.data.map((s) => (
              <Menu.Item key={s.id} onClick={() => save.mutate({ scheduleIds: [...selected.map((x) => x.id), s.id] })}>
                {s.name} <Text span size="xs" c="dimmed">{describeRecurrence(s)}</Text>
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
      )}
      {canEdit && schedules.data.length === 0 && (
        <Anchor component={RouterLink} to="/schedules/new" size="sm">Создать расписание</Anchor>
      )}
    </Group>
  );
}
