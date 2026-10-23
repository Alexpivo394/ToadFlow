import { useSearchParams } from 'react-router';
import { Group, Select, TextInput } from '@mantine/core';
import { useDebouncedCallback } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import type { AgentView, JobStatus, ScheduleView } from '../../api/types';
import { t } from '../../i18n/ru';
import { fromSearchParams, toSearchParams, type HistoryFilters, type Period } from './filters';

export function useHistoryFilters(): [HistoryFilters, (patch: Partial<HistoryFilters>) => void] {
  const [params, setParams] = useSearchParams();
  const filters = fromSearchParams(params);
  // Любой новый фильтр возвращает на первую страницу.
  const update = (patch: Partial<HistoryFilters>) => setParams(toSearchParams({ ...filters, page: 1, ...patch }), { replace: true });
  return [filters, update];
}

export function Filters() {
  const [filters, update] = useHistoryFilters();
  const schedules = useApi<ScheduleView[]>(['schedules'], '/api/schedules');
  const agents = useApi<AgentView[]>(['agents'], '/api/agents');
  const search = useDebouncedCallback((value: string) => update({ search: value }), 400);

  return (
    <Group mb="md" gap="sm">
      <Select
        w={150}
        value={filters.period}
        allowDeselect={false}
        onChange={(v) => update({ period: v as Period })}
        data={[
          { value: '1d', label: 'За сутки' },
          { value: '7d', label: 'За 7 дней' },
          { value: '30d', label: 'За 30 дней' },
          { value: 'all', label: 'За всё время' },
        ]}
      />
      <Select
        w={220}
        placeholder="Все расписания"
        clearable
        value={filters.scheduleId ?? null}
        onChange={(v) => update({ scheduleId: v ?? undefined })}
        data={(schedules.data ?? []).map((s) => ({ value: s.id, label: s.name }))}
      />
      <Select
        w={190}
        placeholder="Любой статус"
        clearable
        value={filters.status ?? null}
        onChange={(v) => update({ status: (v as JobStatus) ?? undefined })}
        data={Object.entries(t.jobStatus).map(([value, label]) => ({ value, label }))}
      />
      <Select
        w={170}
        placeholder="Все агенты"
        clearable
        value={filters.agentId ?? null}
        onChange={(v) => update({ agentId: v ?? undefined })}
        data={(agents.data ?? []).map((a) => ({ value: a.id, label: a.machineName }))}
      />
      <TextInput w={220} placeholder="имя модели…" leftSection={<IconSearch size={14} />} defaultValue={filters.search}
        onChange={(e) => search(e.currentTarget.value)} />
    </Group>
  );
}
