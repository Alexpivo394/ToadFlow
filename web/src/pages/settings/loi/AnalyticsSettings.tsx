import { useEffect, useState } from 'react';
import { Autocomplete, Button, Checkbox, Group, NumberInput, Stack, Table, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../../api/queries';
import type { AnalyticsFilters, AnalyticsSettings as Settings, KnownModel, ModelOverride } from '../../../api/types';
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

/** Модель поправки: путь — одна модель, имя без папок — все модели с этим именем. Так же решает сервер. */
const isPath = (text: string) => /[\\/:]/.test(text);

/** Имя модели без папок и расширения. */
const modelNameOf = (text: string) => text.split(/[\\/]/).pop()!.replace(/\.rvt$/i, '');

/** Ручные поправки: когда имя модели не разобрать справочником, объект и раздел задаются ей напрямую. */
function OverridesPanel({ readOnly }: { readOnly: boolean }) {
  const overrides = useApi<ModelOverride[]>(['loi-analytics', 'overrides'], '/api/loi/models/overrides');
  const known = useApi<KnownModel[]>(['loi-analytics', 'known-models'], readOnly ? null : '/api/loi/naming/models');
  const filters = useApi<AnalyticsFilters>(['loi-analytics', 'filters'], readOnly ? null : '/api/loi/analytics/filters');
  const disciplines = useApi<string[]>(['loi-analytics', 'disciplines'], readOnly ? null : '/api/loi/disciplines');
  const empty = { modelPath: '', customer: '', object: '', discipline: '', part: '' };
  const [form, setForm] = useState(empty);
  const save = useApiMutation<typeof empty & { modelName: string }>('PUT', () => '/api/loi/models/overrides', { invalidate: ['loi-analytics'], success: 'Поправка сохранена' });
  const remove = useApiMutation<string>('DELETE', (key) => `/api/loi/models/overrides?key=${encodeURIComponent(key)}`, { invalidate: ['loi-analytics'] });

  const models = known.data ?? [];
  const modelOptions = [
    { group: 'Одна модель (по пути)', items: [...new Set(models.map((m) => m.path).filter(Boolean))] },
    { group: 'Все модели с именем', items: [...new Set(models.map((m) => m.name))] },
  ].filter((g) => g.items.length > 0);
  const model = form.modelPath.trim();
  const unique = (values: (string | null | undefined)[]) => [...new Set(values.filter((v): v is string => Boolean(v)))];

  return (
    <Panel title="Ручные поправки моделей">
      <Text size="sm" c="dimmed" mb="sm">
        Объект, раздел, часть или заказчик модели, которую справочник не разбирает или разбирает неправильно. Поправка главнее справочника — и в аналитике,
        и при выборе профиля по разделу в расписании. Пустое поле — как по справочнику. Записанные проверки пересчитываются сразу.
      </Text>
      {(overrides.data ?? []).length === 0 ? <Empty>Поправок нет</Empty> : (
        <Table mb="sm">
          <Table.Tbody>
            {overrides.data!.map((o) => (
              <Table.Tr key={o.modelKey}>
                <Table.Td>
                  <Text size="sm" fw={600}>{o.modelName}</Text>
                  <Text size="xs" c="dimmed">{o.byName ? 'все модели с этим именем' : o.modelKey}</Text>
                </Table.Td>
                <Table.Td>{[o.customer, o.object, o.discipline, o.part].filter(Boolean).join(' · ')}</Table.Td>
                <Table.Td c="dimmed">{formatDateTime(o.updatedAt)}</Table.Td>
                <Table.Td>
                  {!readOnly && (
                    <Group justify="flex-end" gap={4} wrap="nowrap">
                      <Button size="compact-xs" variant="subtle"
                        onClick={() => setForm({ modelPath: o.byName ? o.modelName : o.modelKey, customer: o.customer ?? '', object: o.object ?? '',
                          discipline: o.discipline ?? '', part: o.part ?? '' })}>
                        Изменить
                      </Button>
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
        <Stack gap="xs">
          <Autocomplete label="Модель" placeholder="Путь RSN://… или \\nas\… — либо имя модели" data={modelOptions} limit={50} value={form.modelPath}
            description={!model ? 'Путь — поправка только этой модели. Имя без папок — всех моделей с таким именем, где бы они ни лежали'
              : isPath(model) ? 'Поправка только этой модели' : `Поправка всех моделей с именем «${modelNameOf(model)}»`}
            onChange={(modelPath) => setForm({ ...form, modelPath })} />
          <Group align="flex-end" gap="xs">
            <Autocomplete label="Заказчик" w={170} data={unique(filters.data?.customers ?? [])} value={form.customer} onChange={(customer) => setForm({ ...form, customer })} />
            <Autocomplete label="Объект" w={190} data={unique((filters.data?.objects ?? []).map((o) => o.name))} value={form.object}
              onChange={(object) => setForm({ ...form, object })} />
            <Autocomplete label="Раздел" w={130} data={unique([...(disciplines.data ?? []), ...(filters.data?.disciplines ?? [])])} value={form.discipline}
              onChange={(discipline) => setForm({ ...form, discipline })} />
            <Autocomplete label="Часть" w={150} data={unique(filters.data?.parts ?? [])} value={form.part} onChange={(part) => setForm({ ...form, part })} />
            <Button disabled={!model} loading={save.isPending}
              onClick={() => save.mutate({ ...form, modelPath: model, modelName: modelNameOf(model) }, { onSuccess: () => setForm(empty) })}>
              Сохранить поправку
            </Button>
            {Object.values(form).some(Boolean) && <Button variant="default" onClick={() => setForm(empty)}>Очистить</Button>}
          </Group>
        </Stack>
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
