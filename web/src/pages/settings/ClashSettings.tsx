import { useEffect, useState } from 'react';
import { Button, NumberInput, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { ClashOverviewSettings, ClashSettingsView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';

const between = (value: number, min: number, max: number) => Number.isFinite(value) && value >= min && value <= max;

/** Хранение выгрузок геометрии и пороги обзора координатора. Меняет администратор. */
export function ClashSettings() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  const saved = useApi<ClashSettingsView>(['clash-settings'], '/api/clash/settings');
  const [draft, setDraft] = useState<ClashSettingsView | null>(null);
  const save = useApiMutation<ClashSettingsView>('PUT', () => '/api/clash/settings', { invalidate: ['clash-settings', 'clash-models'], success: 'Настройки коллизий сохранены' });
  const value = draft ?? saved.data;

  useEffect(() => {
    if (saved.data && !draft) setDraft(saved.data);
  }, [saved.data, draft]);

  if (!value) return null;
  const o = value.overview;
  const setOverview = (patch: Partial<ClashOverviewSettings>) => setDraft({ ...value, overview: { ...o, ...patch } });
  const keepInvalid = !between(value.storage.keepSnapshots, 1, 20);
  const errors = {
    staleDays: !between(o.staleDays, 1, 365),
    noRunDays: !between(o.noRunDays, 1, 365),
    spikeShare: !(o.spikeShare > 0 && o.spikeShare <= 1),
    spikeMin: !between(o.spikeMin, 0, 1_000_000),
    stuckDays: !between(o.stuckDays, 1, 3650),
  };
  const invalid = keepInvalid || Object.values(errors).some(Boolean);

  return (
    <Panel title="Коллизии">
      <Stack gap="sm" maw={640}>
        <Text size="sm" c="dimmed">
          Каждая выгрузка модели — геометрия и параметры элементов — хранится на сервере. Для истории коллизий нужны только отпечатки геометрии,
          поэтому старые выгрузки можно удалять.
        </Text>
        <NumberInput label="Сколько выгрузок хранить у каждой модели" w={320} min={1} max={20} value={value.storage.keepSnapshots} disabled={readOnly}
          error={keepInvalid ? 'От 1 до 20' : null} onChange={(v) => setDraft({ ...value, storage: { keepSnapshots: Number(v) } })} />
        <Title order={5} mt="sm">Обзор координатора</Title>
        <Text size="sm" c="dimmed">Когда проверка попадает в «Настройка проверок → Обзор» и в блок «Коллизии: требует внимания».</Text>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <NumberInput label="Выгрузка устарела — дней" min={1} max={365} value={o.staleDays} disabled={readOnly}
            error={errors.staleDays ? 'От 1 до 365' : null} onChange={(v) => setOverview({ staleDays: Number(v) })} />
          <NumberInput label="Давно не прогонялась — дней" min={1} max={365} value={o.noRunDays} disabled={readOnly}
            error={errors.noRunDays ? 'От 1 до 365' : null} onChange={(v) => setOverview({ noRunDays: Number(v) })} />
          <NumberInput label="Всплеск — доля новых от активных, %" min={1} max={100} value={Math.round(o.spikeShare * 100)} disabled={readOnly}
            error={errors.spikeShare ? 'От 1 до 100 %' : null} onChange={(v) => setOverview({ spikeShare: Number(v) / 100 })} />
          <NumberInput label="Всплеск — и не меньше новых" min={0} value={o.spikeMin} disabled={readOnly}
            error={errors.spikeMin ? 'От 0' : null} onChange={(v) => setOverview({ spikeMin: Number(v) })} />
          <NumberInput label="Висят — дней без движения" min={1} max={3650} value={o.stuckDays} disabled={readOnly}
            error={errors.stuckDays ? 'От 1 до 3650' : null} onChange={(v) => setOverview({ stuckDays: Number(v) })} />
        </SimpleGrid>
        {!readOnly && (
          <Button w="fit-content" disabled={invalid} loading={save.isPending} onClick={() => save.mutate(value)}>Сохранить</Button>
        )}
      </Stack>
    </Panel>
  );
}
