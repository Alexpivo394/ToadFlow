import { useEffect, useState } from 'react';
import { Button, NumberInput, Stack, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { ClashStorageSettings } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';

/** Сколько выгрузок геометрии хранить у каждой модели: старшие удаляются сразу после сохранения. */
export function ClashSettings() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  const saved = useApi<ClashStorageSettings>(['clash-settings'], '/api/clash/settings');
  const [draft, setDraft] = useState<ClashStorageSettings | null>(null);
  const save = useApiMutation<ClashStorageSettings>('PUT', () => '/api/clash/settings', { invalidate: ['clash-settings', 'clash-models'], success: 'Настройки коллизий сохранены' });
  const value = draft ?? saved.data;

  useEffect(() => {
    if (saved.data && !draft) setDraft(saved.data);
  }, [saved.data, draft]);

  if (!value) return null;
  const invalid = !(value.keepSnapshots >= 1 && value.keepSnapshots <= 20);
  return (
    <Panel title="Коллизии">
      <Stack gap="sm" maw={560}>
        <Text size="sm" c="dimmed">
          Каждая выгрузка модели — геометрия и параметры элементов — хранится на сервере. Для истории коллизий нужны только отпечатки геометрии,
          поэтому старые выгрузки можно удалять.
        </Text>
        <NumberInput label="Сколько выгрузок хранить у каждой модели" w={320} min={1} max={20} value={value.keepSnapshots} disabled={readOnly}
          error={invalid ? 'От 1 до 20' : null} onChange={(v) => setDraft({ keepSnapshots: Number(v) })} />
        {!readOnly && (
          <Button w="fit-content" disabled={invalid} loading={save.isPending} onClick={() => save.mutate(value)}>Сохранить</Button>
        )}
      </Stack>
    </Panel>
  );
}
