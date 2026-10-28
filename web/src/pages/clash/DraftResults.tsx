import { useState } from 'react';
import { Button, Group, NumberInput, Pagination, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { DraftHitView, DraftSummary, HitPage } from '../../api/types';
import { Empty, Panel } from '../../components/ui';
import { HitViewer } from './viewer/HitViewer';

const PageSize = 50;

interface Filters {
  disciplines: string | null;
  sets: string | null;
  level: string;
  minDepth: number | null;
  sort: 'number' | 'depth';
}

function Side({ hit, side }: { hit: DraftHitView; side: 'A' | 'B' }) {
  const a = side === 'A';
  return (
    <Stack gap={0}>
      <Text size="sm">{a ? hit.categoryA : hit.categoryB} · {(a ? hit.familyA : hit.familyB) ?? '—'} · {(a ? hit.typeA : hit.typeB) ?? '—'}</Text>
      <Text size="xs" c="dimmed">
        {a ? hit.modelNameA : hit.modelNameB} · ID {a ? hit.elementIdA : hit.elementIdB} · {(a ? hit.setNamesA : hit.setNamesB).join(', ')}
      </Text>
    </Stack>
  );
}

/** Результат черновика: сводка по парам разделов и наборов (щелчок — фильтр) и список коллизий страницами. */
export function DraftResults({ partId }: { partId: string }) {
  const [filters, setFilters] = useState<Filters>({ disciplines: null, sets: null, level: '', minDepth: null, sort: 'number' });
  const [page, setPage] = useState(1);
  const [opened, setOpened] = useState<number | null>(null);
  const summary = useApi<DraftSummary>(['clash-parts', partId, 'summary'], `/api/clash/parts/${partId}/draft/summary`);

  const query = new URLSearchParams({ page: String(page), pageSize: String(PageSize), sort: filters.sort });
  if (filters.disciplines) query.set('disciplines', filters.disciplines);
  if (filters.sets) {
    const [a, b] = filters.sets.split('|');
    query.set('setA', a);
    query.set('setB', b);
  }
  if (filters.level.trim()) query.set('level', filters.level.trim());
  if (filters.minDepth != null) query.set('minDepth', String(filters.minDepth));
  const hits = useApi<HitPage>(['clash-parts', partId, 'hits', query.toString()], `/api/clash/parts/${partId}/draft/hits?${query}`);

  const filter = (patch: Partial<Filters>) => {
    setFilters({ ...filters, ...patch });
    setPage(1);
  };

  if (!summary.data || summary.data.total === 0) return <Empty>Результатов пока нет — нажмите «Прогнать»</Empty>;
  const pages = Math.max(1, Math.ceil((hits.data?.total ?? 0) / PageSize));

  return (
    <Stack gap="md">
      <Panel title={`Коллизий: ${summary.data.total.toLocaleString('ru-RU')}`}>
        <Stack gap="xs">
          <Group gap="xs">
            {summary.data.byDisciplines.map((p) => (
              <Button key={p.disciplinePair} size="xs" variant={filters.disciplines === p.disciplinePair ? 'filled' : 'light'}
                onClick={() => filter({ disciplines: filters.disciplines === p.disciplinePair ? null : p.disciplinePair })}>
                {p.disciplinePair}: {p.count.toLocaleString('ru-RU')} (допуск {p.toleranceMm} мм)
              </Button>
            ))}
          </Group>
          <Group gap="xs">
            {summary.data.bySets.slice(0, 30).map((s) => {
              const key = `${s.setA}|${s.setB}`;
              return (
                <Button key={key} size="compact-xs" variant={filters.sets === key ? 'filled' : 'subtle'} onClick={() => filter({ sets: filters.sets === key ? null : key })}>
                  {s.setAName} × {s.setBName}: {s.count.toLocaleString('ru-RU')}
                </Button>
              );
            })}
          </Group>
        </Stack>
      </Panel>

      <Group gap="sm" align="flex-end">
        <TextInput size="xs" label="Уровень" w={180} value={filters.level} onChange={(e) => filter({ level: e.currentTarget.value })} />
        <NumberInput size="xs" label="Глубина от, мм" w={140} min={0} value={filters.minDepth ?? ''}
          onChange={(v) => filter({ minDepth: v === '' ? null : Number(v) })} />
        <Select size="xs" label="Порядок" w={170} allowDeselect={false} value={filters.sort}
          data={[{ value: 'number', label: 'по номеру' }, { value: 'depth', label: 'глубже сначала' }]} onChange={(v) => filter({ sort: v as Filters['sort'] })} />
        <Text size="sm" c="dimmed">Найдено: {(hits.data?.total ?? 0).toLocaleString('ru-RU')}</Text>
      </Group>

      <Table striped>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>№</Table.Th>
            <Table.Th ta="right">Глубина</Table.Th>
            <Table.Th>Элемент A</Table.Th>
            <Table.Th>Элемент B</Table.Th>
            <Table.Th>Уровень</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {(hits.data?.items ?? []).map((h, i) => (
            <Table.Tr key={h.number} onClick={() => setOpened(i)} style={{ cursor: 'pointer' }} title="Открыть в 3D">
              <Table.Td>{h.number}</Table.Td>
              <Table.Td ta="right">{Math.round(h.depthMm)} мм</Table.Td>
              <Table.Td><Side hit={h} side="A" /></Table.Td>
              <Table.Td><Side hit={h} side="B" /></Table.Td>
              <Table.Td>{h.levelA ?? h.levelB ?? '—'}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {pages > 1 && <Pagination total={pages} value={page} onChange={setPage} />}
      {opened != null && hits.data?.items[opened] && (
        <HitViewer runId={hits.data.items[opened].runId} hits={hits.data.items} index={opened} onIndex={setOpened} onClose={() => setOpened(null)} />
      )}
    </Stack>
  );
}
