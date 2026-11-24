import { useEffect, useState } from 'react';
import { Button, Group, Stack, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';
import { t } from '../../i18n/ru';
import type { UsageSettings } from './api';

/** Настройки «Аналитики»: срок хранения данных. Видят все, кому виден модуль, меняет администратор. */
export function SettingsPage() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  const saved = useApi<UsageSettings>(['usage', 'settings'], '/api/usage/settings');
  const [text, setText] = useState<string | null>(null);
  const save = useApiMutation<UsageSettings>('PUT', () => '/api/usage/settings', { invalidate: ['usage'], success: t.usage.saved });

  useEffect(() => {
    if (saved.data && text === null) setText(String(saved.data.retentionMonths));
  }, [saved.data, text]);

  const raw = text ?? String(saved.data?.retentionMonths ?? 13);
  const value = /^\d+$/.test(raw.trim()) ? Number(raw.trim()) : NaN;
  const invalid = !(Number.isInteger(value) && value >= 1 && value <= 120);

  return (
    <Panel>
      <Stack gap="md" maw={420}>
        <TextInput label={t.usage.retention} description={t.usage.retentionHint} inputMode="numeric" disabled={readOnly}
          value={raw} error={invalid ? t.usage.retentionRange : null} onChange={(e) => setText(e.currentTarget.value)} />
        {!readOnly && (
          <Group>
            <Button disabled={invalid} loading={save.isPending} onClick={() => save.mutate({ retentionMonths: value })}>{t.usage.save}</Button>
          </Group>
        )}
      </Stack>
    </Panel>
  );
}
