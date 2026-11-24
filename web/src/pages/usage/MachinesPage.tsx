import { Stack, Text } from '@mantine/core';
import { useNavigate } from 'react-router';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryState } from '../../components/QueryState';
import { formatDateTime } from '../../lib/format';
import { t } from '../../i18n/ru';
import type { MachineRow, MachinesView } from './api';
import { EmptyState } from './EmptyState';
import { FilterBar } from './FilterBar';
import { formatDuration, formatMemory } from './format';
import { Block, CsvButton, PersonLink } from './parts';
import { useUsageFilter, withQuery } from './useUsageFilter';

/** «Машины»: парк компьютеров проектировщиков — железо и как на нём ждут; сначала самые медленные по открытию. */
export function MachinesPage() {
  const { query } = useUsageFilter();
  const path = withQuery('/api/usage/machines', query);
  const machines = useApi<MachinesView>(['usage', 'machines', path], path, { keepPrevious: true });
  const navigate = useNavigate();

  const columns: Column<MachineRow>[] = [
    { key: 'name', title: t.usage.machine, render: (r) => <Text size="sm" fw={500}>{r.name}</Text>, sort: (r) => r.name },
    { key: 'cpu', title: t.usage.cpu, render: (r) => <Text size="sm">{r.cpu ?? '—'}{r.cores ? ` · ${r.cores}` : ''}</Text>, sort: (r) => r.cpu },
    { key: 'ram', title: t.usage.ram, render: (r) => formatMemory(r.ramMb), sort: (r) => r.ramMb },
    { key: 'gpu', title: t.usage.gpu, render: (r) => <Text size="sm">{r.gpu ?? '—'}{r.gpuMemoryMb ? ` · ${formatMemory(r.gpuMemoryMb)}` : ''}</Text>, sort: (r) => r.gpu },
    { key: 'disk', title: t.usage.disk, render: (r) => `${t.usage.diskKinds[r.diskKind]}${r.diskFreeGb != null ? ` · ${r.diskFreeGb} ГБ своб.` : ''}`, sort: (r) => r.diskKind },
    { key: 'years', title: t.usage.revitYears, render: (r) => r.revitYears.join(', ') || '—' },
    { key: 'toadTools', title: t.usage.toadTools, render: (r) => r.toadTools ?? '—', sort: (r) => r.toadTools },
    { key: 'open', title: t.usage.openMedian, render: (r) => formatDuration(r.openMedianMs), sort: (r) => r.openMedianMs },
    { key: 'sync', title: t.usage.syncMedian, render: (r) => formatDuration(r.syncMedianMs), sort: (r) => r.syncMedianMs },
    { key: 'abnormal', title: t.usage.abnormal, render: (r) => r.abnormal || '—', sort: (r) => r.abnormal },
    { key: 'lastSeen', title: t.usage.lastSeen, render: (r) => formatDateTime(r.lastSeenAt), sort: (r) => r.lastSeenAt },
    { key: 'lastUser', title: t.usage.lastUser, render: (r) => (r.lastUser ? <PersonLink id={r.lastUser.id} name={r.lastUser.name} /> : '—'), sort: (r) => r.lastUser?.name ?? null },
  ];

  return (
    <Stack gap="md">
      <FilterBar basis={machines.data?.basis} />
      <QueryState query={machines}>
        {(view) => view.rows.length === 0 ? <EmptyState /> : (
          <Block actions={(
            <CsvButton fileName="машины"
              header={[t.usage.machine, t.usage.cpu, 'Ядер', `${t.usage.ram}, МБ`, t.usage.gpu, t.usage.disk, `Свободно, ГБ`, t.usage.os, t.usage.revitYears,
                t.usage.toadTools, `${t.usage.openMedian}, с`, `${t.usage.syncMedian}, с`, t.usage.sessions, t.usage.abnormal, t.usage.activeHours,
                t.usage.lastSeen, t.usage.lastUser]}
              rows={() => view.rows.map((r) => [r.name, r.cpu, r.cores, r.ramMb, r.gpu, t.usage.diskKinds[r.diskKind], r.diskFreeGb, r.os, r.revitYears.join(', '),
                r.toadTools, sec(r.openMedianMs), sec(r.syncMedianMs), r.sessions, r.abnormal, r.activeHours, formatDateTime(r.lastSeenAt), r.lastUser?.name])} />
          )}>
            <DataTable rows={slowestFirst(view.rows)} columns={columns} rowKey={(r) => r.key} search={(r) => `${r.name} ${r.cpu ?? ""} ${r.gpu ?? ""}`}
              onRowClick={(r) => navigate(`/usage/machines/${encodeURIComponent(r.key)}`)} />
          </Block>
        )}
      </QueryState>
    </Stack>
  );
}

const sec = (ms: number | null) => (ms == null ? null : Math.round(ms / 1000));

/** По умолчанию — самые медленные по открытию сверху; машины без открытий за период — в конце. */
const slowestFirst = (rows: MachineRow[]) => [...rows].sort((a, b) => (b.openMedianMs ?? -1) - (a.openMedianMs ?? -1));
