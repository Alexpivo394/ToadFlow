import { Group, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { DatePickerInput } from '@mantine/dates';
import { useApi } from '../../api/queries';
import { t } from '../../i18n/ru';
import type { UsageBasis, UsageFilters } from './api';
import { useUsageFilter, type UsagePeriod } from './useUsageFilter';

/**
 * Общий верх экранов «Аналитики»: период, отборы раздела, человека, машины и года Revit, строка «построено по …».
 * <c>periodless</c> — экран не зависит от периода («Охват»): остаётся только раздел.
 */
export function FilterBar({ basis, periodless }: { basis?: UsageBasis | null; periodless?: boolean }) {
  const { filter, setFilter } = useUsageFilter();
  const options = useApi<UsageFilters>(['usage', 'filters'], '/api/usage/filters').data;
  const range: [string | null, string | null] = [filter.from ?? null, filter.to ?? null];

  return (
    <Stack gap={6} mb="md">
      <Group gap="sm" align="flex-end" wrap="wrap">
        {!periodless && (
          <>
            <SegmentedControl aria-label={t.usage.period} value={filter.period}
              data={(['7d', '30d', '90d', 'custom'] as UsagePeriod[]).map((value) => ({ value, label: t.usage.periods[value] }))}
              onChange={(value) => setFilter({ period: value as UsagePeriod })} />
            {filter.period === 'custom' && (
              <DatePickerInput type="range" w={240} aria-label={t.usage.range} placeholder={t.usage.range} valueFormat="DD.MM.YYYY" value={range}
                onChange={([from, to]) => setFilter({ from: from ? String(from).slice(0, 10) : undefined, to: to ? String(to).slice(0, 10) : undefined })} />
            )}
          </>
        )}
        <Select w={170} aria-label={t.usage.discipline} placeholder={t.usage.discipline} clearable searchable data={options?.disciplines ?? []}
          value={filter.discipline ?? null} onChange={(value) => setFilter({ discipline: value ?? undefined })} />
        {!periodless && (
          <>
            <Select w={220} aria-label={t.usage.person} placeholder={t.usage.person} clearable searchable
              data={(options?.people ?? []).map((p) => ({ value: p.id, label: p.name }))}
              value={filter.user ?? null} onChange={(value) => setFilter({ user: value ?? undefined })} />
            <Select w={170} aria-label={t.usage.machine} placeholder={t.usage.machine} clearable searchable
              data={(options?.machines ?? []).map((m) => ({ value: m.key, label: m.name }))}
              value={filter.machine ?? null} onChange={(value) => setFilter({ machine: value ?? undefined })} />
            <Select w={130} aria-label={t.usage.revitYear} placeholder={t.usage.revitYear} clearable
              data={(options?.revitYears ?? []).map(String)}
              value={filter.revitYear ?? null} onChange={(value) => setFilter({ revitYear: value ?? undefined })} />
          </>
        )}
      </Group>
      {basis && <Text size="sm" c="dimmed">{t.usage.basis(basis.people, basis.machines)}</Text>}
    </Stack>
  );
}
