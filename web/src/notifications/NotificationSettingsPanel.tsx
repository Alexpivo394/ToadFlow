import { Checkbox, Stack, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../api/queries';
import { Panel } from '../components/ui';
import { t } from '../i18n/ru';
import { notificationsKey, type NotificationSettings } from './api';

/** Профиль: какие уведомления приходят — по группам, галочка сразу сохраняется. */
export function NotificationSettingsPanel() {
  const settings = useApi<NotificationSettings>([notificationsKey, 'settings'], '/api/notifications/settings');
  const save = useApiMutation<string[]>('PUT', () => '/api/notifications/settings', {
    invalidate: [notificationsKey], success: t.notify.saved, json: (muted) => ({ muted }),
  });
  const kinds = settings.data?.kinds ?? [];
  const groups = [...new Set(kinds.map((k) => k.group))];
  const toggle = (kind: string, on: boolean) =>
    save.mutate(kinds.filter((k) => (k.kind === kind ? !on : k.muted)).map((k) => k.kind));

  return (
    <Panel title={t.notify.profileTitle}>
      <Stack gap="sm" id="notifications">
        <Text size="sm" c="dimmed">{t.notify.profileHint}</Text>
        {groups.map((group) => (
          <Stack key={group} gap={6}>
            <Text size="sm" fw={600}>{group}</Text>
            {kinds.filter((k) => k.group === group).map((k) => (
              <Checkbox key={k.kind} label={k.title} checked={!k.muted} disabled={save.isPending}
                onChange={(e) => toggle(k.kind, e.currentTarget.checked)} />
            ))}
          </Stack>
        ))}
      </Stack>
    </Panel>
  );
}
