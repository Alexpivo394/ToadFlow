import { useSearchParams } from 'react-router';
import { Paper, Select, SimpleGrid, Text, TextInput, Tooltip } from '@mantine/core';
import { useDebouncedCallback } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import type { AnalyticsFilters } from '../../api/types';
import { formatShare } from '../../lib/loi';
import { fromSearchParams, noneValue, shareColor, toSearchParams, type AnalyticsFilterState, type AnalyticsPeriod } from './filters';

export function useAnalyticsFilters(): [AnalyticsFilterState, (patch: Partial<AnalyticsFilterState>) => void] {
  const [params, setParams] = useSearchParams();
  const state = fromSearchParams(params);
  const update = (patch: Partial<AnalyticsFilterState>) => {
    const next = { ...state, ...patch };
    const query = toSearchParams(next);
    const key = params.get('key');
    if (key) query.set('key', key); // страница модели держит свою модель
    setParams(query, { replace: true });
  };
  return [state, update];
}

/** Значение поля или «не распознано» — у моделей, которые справочник именования не разобрал. */
export const orNone = (value: string | null | undefined) => value ?? 'не распознано';

export function Share({ value, size = 'sm' }: { value: number | null | undefined; size?: string }) {
  return (
    <Text span size={size} fw={600} c={value == null ? 'dimmed' : `${shareColor(value)}.7`}>
      {formatShare(value)}
    </Text>
  );
}

/** Изменение с прошлой проверки в процентных пунктах; если правила профиля поменялись — с оговоркой. */
export function Delta({ now, before, profileChanged }: { now: number | null; before: number | null; profileChanged?: boolean }) {
  if (now == null || before == null) return <Text span c="dimmed" size="sm">—</Text>;
  const points = Math.round((now - before) * 1000) / 10;
  const text = points === 0 ? '±0' : `${points > 0 ? '▲' : '▼'} ${Math.abs(points).toString().replace('.', ',')}`;
  const body = <Text span size="sm" c={points > 0 ? 'green.7' : points < 0 ? 'red.7' : 'dimmed'}>{text}{profileChanged ? ' *' : ''}</Text>;
  return profileChanged ? <Tooltip label="Между проверками менялись правила профиля — изменение не обязательно заслуга модели">{body}</Tooltip> : body;
}

export function Stat({ title, value, hint }: { title: string; value: React.ReactNode; hint?: string }) {
  return (
    <Paper withBorder p="md" radius="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={700}>{title}</Text>
      <Text size="xl" fw={700}>{value}</Text>
      {hint && <Text size="xs" c="dimmed">{hint}</Text>}
    </Paper>
  );
}

const withNone = (values: string[]) => [...values.map((v) => ({ value: v, label: v })), { value: noneValue, label: 'не распознано' }];

export function AnalyticsFiltersBar() {
  const [state, update] = useAnalyticsFilters();
  const filters = useApi<AnalyticsFilters>(['loi-analytics', 'filters'], '/api/loi/analytics/filters');
  const search = useDebouncedCallback((value: string) => update({ search: value || undefined }), 400);
  const data = filters.data;
  const objects = (data?.objects ?? []).filter((o) => !state.customer || o.customer === state.customer).map((o) => o.name);

  // Сетка в четыре колонки — фильтры ровными рядами над четырьмя карточками итогов.
  return (
    <SimpleGrid mb="md" spacing="sm" verticalSpacing="sm" cols={{ base: 2, sm: 4 }}>
      <Select value={state.period} allowDeselect={false} onChange={(v) => update({ period: v as AnalyticsPeriod })}
        data={[{ value: '30d', label: '30 дней' }, { value: '90d', label: '90 дней' }, { value: '365d', label: 'Год' }, { value: 'all', label: 'Всё время' }]} />
      <Select placeholder="Все заказчики" clearable searchable value={state.customer ?? null} data={withNone(data?.customers ?? [])}
        onChange={(v) => update({ customer: v ?? undefined, object: undefined })} />
      <Select placeholder="Все объекты" clearable searchable value={state.object ?? null} data={withNone(objects)}
        onChange={(v) => update({ object: v ?? undefined })} />
      <Select placeholder="Все разделы" clearable searchable value={state.discipline ?? null} data={withNone(data?.disciplines ?? [])}
        onChange={(v) => update({ discipline: v ?? undefined })} />
      <Select placeholder="Все части" clearable searchable value={state.part ?? null} data={withNone(data?.parts ?? [])}
        onChange={(v) => update({ part: v ?? undefined })} />
      <Select placeholder="Все профили" clearable value={state.profile ?? null} data={data?.profiles ?? []}
        onChange={(v) => update({ profile: v ?? undefined })} />
      <Select placeholder="Любой источник" clearable value={state.source ?? null}
        data={[{ value: 'Schedule', label: 'По расписанию' }, { value: 'Manual', label: 'Вручную' }]}
        onChange={(v) => update({ source: (v as AnalyticsFilterState['source']) ?? undefined })} />
      <TextInput placeholder="Модель" leftSection={<IconSearch size={14} />} defaultValue={state.search} onChange={(e) => search(e.currentTarget.value)} />
    </SimpleGrid>
  );
}
