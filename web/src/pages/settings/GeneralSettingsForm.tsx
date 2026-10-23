import { useEffect, useMemo, useState } from 'react';
import { Button, NumberInput, Select, SimpleGrid, Stack } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { GeneralSettings } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';
import { t } from '../../i18n/ru';

export function GeneralSettingsForm() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  const settings = useApi<GeneralSettings>(['settings'], '/api/settings');
  const [draft, setDraft] = useState<GeneralSettings | null>(null);
  const save = useApiMutation<GeneralSettings>('PUT', () => '/api/settings', { invalidate: ['settings'], success: 'Настройки сохранены и отправлены агентам' });
  const zones = useMemo(() => (typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['Europe/Moscow']), []);

  useEffect(() => {
    if (settings.data) setDraft(settings.data);
  }, [settings.data]);

  if (!draft) return null;
  const set = (patch: Partial<GeneralSettings>) => setDraft({ ...draft, ...patch });
  const number = (key: keyof GeneralSettings, label: string, min: number, description?: string) => (
    <NumberInput label={label} description={description} min={min} value={draft[key] as number} disabled={readOnly}
      onChange={(v) => set({ [key]: Number(v) || min } as Partial<GeneralSettings>)} />
  );

  return (
    <Panel title="Общие">
      <Stack maw={720}>
        <Select label="Часовой пояс" description="По нему считаются расписания, дни на графике и {дата} в папке результатов" searchable
          data={zones.includes(draft.timeZoneId) ? zones : [draft.timeZoneId, ...zones]} value={draft.timeZoneId} allowDeselect={false} disabled={readOnly}
          onChange={(v) => v && set({ timeZoneId: v })} />
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          {number('defaultModelTimeoutMinutes', 'Таймаут на модель по умолчанию, мин', 1)}
          {number('silenceTimeoutMinutes', 'Молчание Revit до перезапуска, мин', 1)}
          {number('logRetentionDays', 'Хранить логи, дней', 1)}
          {number('recycleAfterModels', 'Перезапускать Revit после, моделей', 1, 'Чтобы не копились утечки памяти')}
          {number('recycleMemoryMb', 'Или когда Revit занял, МБ', 1000)}
        </SimpleGrid>
        {!readOnly && (
          <Button w={160} loading={save.isPending} onClick={() => save.mutate(draft)}>
            {t.common.save}
          </Button>
        )}
      </Stack>
    </Panel>
  );
}
