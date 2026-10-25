import { useEffect, useState } from 'react';
import { Button, Checkbox, Group, NumberInput, Stack, Table, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../../api/queries';
import type { AnalyticsSettings as Settings, ModelOverride } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { ConfirmButton, Empty, Panel } from '../../../components/ui';
import { formatDateTime } from '../../../lib/format';
import { NamingEditor } from './NamingEditor';

function SettingsPanel({ readOnly }: { readOnly: boolean }) {
  const saved = useApi<Settings>(['loi-analytics', 'settings'], '/api/loi/analytics/settings');
  const [draft, setDraft] = useState<Settings | null>(null);
  const save = useApiMutation<Settings>('PUT', () => '/api/loi/analytics/settings', { invalidate: ['loi-analytics'], success: 'Настройки аналитики сохранены' });
  const reclassify = useApiMutation<void, { models: number }>('POST', () => '/api/loi/analytics/reclassify', { invalidate: ['loi-analytics'] });
  const value = draft ?? saved.data;

  useEffect(() => {
    if (saved.data && !draft) setDraft(saved.data);
  }, [saved.data, draft]);

  if (!value) return null;
  return (
    <Panel title="Аналитика LOI">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          Итоги проверок LOI — по расписанию и ручных из ParamChecker — складываются в базу, по ним строится страница «Аналитика LOI». Книга Excel
          для дашбордов в расписании ведётся независимо от этого.
        </Text>
        <Checkbox label="Сохранять итоги проверок и показывать аналитику" checked={value.enabled} disabled={readOnly}
          onChange={(e) => setDraft({ ...value, enabled: e.currentTarget.checked })} />
        <NumberInput label="Хранить проверки, лет" description="0 — всегда" w={220} min={0} max={100} value={value.retentionYears} disabled={readOnly}
          onChange={(v) => setDraft({ ...value, retentionYears: Number(v) || 0 })} />
        {!readOnly && (
          <Group>
            <Button onClick={() => save.mutate(value)} loading={save.isPending}>Сохранить</Button>
            <Button variant="default" loading={reclassify.isPending}
              onClick={() => reclassify.mutate(undefined)}>
              Пересчитать аналитику по справочнику
            </Button>
            {reclassify.data && <Text size="sm" c="dimmed">Пересчитано моделей: {reclassify.data.models}</Text>}
          </Group>
        )}
      </Stack>
    </Panel>
  );
}

/** Ручные поправки: когда имя модели не разобрать справочником, объект и раздел задаются ей напрямую. */
function OverridesPanel({ readOnly }: { readOnly: boolean }) {
  const overrides = useApi<ModelOverride[]>(['loi-analytics', 'overrides'], '/api/loi/models/overrides');
  const empty = { modelName: '', modelPath: '', customer: '', object: '', discipline: '', part: '' };
  const [form, setForm] = useState(empty);
  const save = useApiMutation<typeof empty>('PUT', () => '/api/loi/models/overrides', { invalidate: ['loi-analytics'], success: 'Поправка сохранена' });
  const remove = useApiMutation<string>('DELETE', (key) => `/api/loi/models/overrides?key=${encodeURIComponent(key)}`, { invalidate: ['loi-analytics'] });

  return (
    <Panel title="Ручные поправки моделей">
      <Text size="sm" c="dimmed" mb="sm">Главнее справочника. Пустое поле — как по справочнику.</Text>
      {(overrides.data ?? []).length === 0 ? <Empty>Поправок нет</Empty> : (
        <Table mb="sm">
          <Table.Tbody>
            {overrides.data!.map((o) => (
              <Table.Tr key={o.modelKey}>
                <Table.Td fw={600}>{o.modelName}</Table.Td>
                <Table.Td>{[o.customer, o.object, o.discipline, o.part].filter(Boolean).join(' · ')}</Table.Td>
                <Table.Td c="dimmed">{formatDateTime(o.updatedAt)}</Table.Td>
                <Table.Td>
                  {!readOnly && (
                    <Group justify="flex-end">
                      <ConfirmButton size="compact-xs" variant="subtle" question={`Убрать поправку модели ${o.modelName}?`} onConfirm={() => remove.mutate(o.modelKey)}>
                        Убрать
                      </ConfirmButton>
                    </Group>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {!readOnly && (
        <Group align="flex-end" gap="xs">
          <TextInput label="Модель" placeholder="Имя или путь RSN://…" w={260} value={form.modelPath}
            onChange={(e) => setForm({ ...form, modelPath: e.currentTarget.value, modelName: e.currentTarget.value.split(/[\\/]/).pop()?.replace(/\.rvt$/i, '') ?? '' })} />
          <TextInput label="Заказчик" w={150} value={form.customer} onChange={(e) => setForm({ ...form, customer: e.currentTarget.value })} />
          <TextInput label="Объект" w={150} value={form.object} onChange={(e) => setForm({ ...form, object: e.currentTarget.value })} />
          <TextInput label="Раздел" w={110} value={form.discipline} onChange={(e) => setForm({ ...form, discipline: e.currentTarget.value })} />
          <TextInput label="Часть" w={130} value={form.part} onChange={(e) => setForm({ ...form, part: e.currentTarget.value })} />
          <Button disabled={!form.modelPath.trim()} onClick={() => save.mutate(form, { onSuccess: () => setForm(empty) })}>Сохранить поправку</Button>
        </Group>
      )}
    </Panel>
  );
}

export function AnalyticsSettings() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  return (
    <Stack>
      <SettingsPanel readOnly={readOnly} />
      <NamingEditor readOnly={readOnly} />
      <OverridesPanel readOnly={readOnly} />
    </Stack>
  );
}
