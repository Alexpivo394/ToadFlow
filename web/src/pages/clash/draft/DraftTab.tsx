import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { Alert, Box, Group, NavLink, NumberInput, ScrollArea, Select, Stack, Text, UnstyledButton } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useApi } from '../../../api/queries';
import type { CheckCount, DraftFacets, DraftHitView, DraftSummary, SetPairCount } from '../../../api/types';
import { Empty } from '../../../components/ui';
import { categoryLabel } from '../../../lib/categories';
import { DraftRunPanel } from '../DraftRunPanel';
import { HitScene, prefetchHit } from '../viewer/HitScene';
import { defaultFilters, type HitFilters, PageSize, useHitNavigation } from './useHitNavigation';

const n = (value: number) => value.toLocaleString('ru-RU');

/** Проверки в порядке матрицы, сгруппированные по группе строки; пустые — серым, чтобы было видно, что их проверили. */
function ChecksList({ checks, selected, onSelect }: { checks: CheckCount[]; selected: string | null; onSelect: (key: string) => void }) {
  const rows = new Map<string, CheckCount[]>();
  for (const c of checks) rows.set(c.groupAName, [...(rows.get(c.groupAName) ?? []), c]);
  return (
    <ScrollArea h="100%" type="auto">
      {[...rows].map(([group, list]) => (
        <Fragment key={group}>
          <Text size="xs" c="dimmed" px="sm" pt="xs">{group}</Text>
          {list.map((c) => (
            <NavLink key={c.key} active={c.key === selected} onClick={() => onSelect(c.key)} py={4}
              c={c.count === 0 ? 'dimmed' : undefined}
              label={<Text size="sm" span>{c.groupAName} × {c.groupBName} <Text span size="xs" c="dimmed">{c.toleranceMm}</Text></Text>}
              rightSection={<Text size="sm" c={c.count === 0 ? 'dimmed' : undefined}>{n(c.count)}</Text>} />
          ))}
        </Fragment>
      ))}
    </ScrollArea>
  );
}

function Side({ hit, side }: { hit: DraftHitView; side: 'A' | 'B' }) {
  const a = side === 'A';
  const family = (a ? hit.familyA : hit.familyB) ?? (a ? hit.typeA : hit.typeB) ?? '—';
  return (
    <Text size="xs" c={a ? 'red.7' : 'blue.7'} truncate>
      {categoryLabel(a ? hit.categoryA : hit.categoryB)} · {family} · ID {a ? hit.elementIdA : hit.elementIdB}
    </Text>
  );
}

/**
 * Черновик: строка прогона и три колонки — проверки, коллизии выбранной проверки (группы по парам наборов, фильтры из
 * результата), 3D выбранной коллизии. ↑/↓ листают коллизии сквозь страницы и проверки, Enter — свойства A, затем B.
 */
export function DraftTab({ partId, readOnly, dirty, onSave }: { partId: string; readOnly: boolean; dirty: boolean; onSave: () => Promise<void> }) {
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [filters, setFilters] = useState<HitFilters>(defaultFilters);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [focus, setFocus] = useState<'A' | 'B' | null>(null);
  const summary = useApi<DraftSummary>(['clash-parts', partId, 'summary'], `/api/clash/parts/${partId}/draft/summary`);
  const checks = useMemo(() => summary.data?.checks ?? [], [summary.data]);

  const check = params.get('check') ?? checks.find((c) => c.count > 0)?.key ?? null;
  const hitParam = params.get('hit');
  const number = hitParam ? Number(hitParam) : null;
  const update = useCallback((patch: Record<string, string | null>) =>
    setParams((p) => {
      const next = new URLSearchParams(p);
      for (const [k, v] of Object.entries(patch)) if (v == null) next.delete(k); else next.set(k, v);
      return next;
    }, { replace: true }), [setParams]);
  const onCheck = useCallback((key: string) => update({ check: key, hit: null }), [update]);
  const onNumber = useCallback((value: number | null) => {
    setFocus(null);
    update({ hit: value == null ? null : String(value) });
  }, [update]);

  const nav = useHitNavigation({ partId, checks, check, number, filters, onCheck, onNumber });
  const facets = useApi<DraftFacets>(['clash-parts', partId, 'facets', check], check ? `/api/clash/parts/${partId}/draft/facets?check=${check}` : null);
  const setPairs = useApi<SetPairCount[]>(['clash-parts', partId, 'setpairs', check], check ? `/api/clash/parts/${partId}/draft/checks/${check}/setpairs` : null);
  const pairNames = new Map((setPairs.data ?? []).map((p) => [`${p.setA.replaceAll('-', '')}|${p.setB.replaceAll('-', '')}`, p]));

  useEffect(() => {
    if (nav.upcoming) prefetchHit(queryClient, nav.upcoming.runId, nav.upcoming.number);
  }, [nav.upcoming, queryClient]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
      if (e.key === 'ArrowDown') { e.preventDefault(); nav.next(); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); nav.prev(); }
      else if (e.key === 'Enter') setFocus((f) => (f === 'A' ? 'B' : 'A'));
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [nav]);

  const current = nav.current;
  const filter = (patch: Partial<HitFilters>) => setFilters({ ...filters, ...patch });
  const checkInfo = checks.find((c) => c.key === check);

  return (
    <Stack gap="sm">
      <DraftRunPanel partId={partId} readOnly={readOnly} dirty={dirty} onSave={onSave} />
      {summary.data?.legacy && <Alert color="orange">Результат получен до появления групп проверки — прогоните черновик заново, чтобы разложить коллизии по проверкам.</Alert>}
      {summary.data && summary.data.total === 0 && !summary.data.legacy ? (
        <Empty>Коллизий пока нет — нажмите «Прогнать»</Empty>
      ) : (
        <Group align="stretch" gap={0} wrap="nowrap" h="calc(100vh - 290px)" mih={520}
          style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
          <Box w={250} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
            <Text size="xs" fw={700} c="dimmed" tt="uppercase" px="sm" pt="sm">Проверки</Text>
            <ChecksList checks={checks} selected={check} onSelect={onCheck} />
          </Box>

          <Stack gap={0} w={430} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
            <Group gap={6} p="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
              <Select size="xs" w={120} clearable placeholder="все" label="Уровень" value={filters.level} onChange={(v) => filter({ level: v })}
                data={(facets.data?.levels ?? []).map((v) => ({ value: v.value, label: `${v.value} · ${n(v.count)}` }))} />
              <Select size="xs" w={120} clearable placeholder="все" label="Модель" value={filters.model} onChange={(v) => filter({ model: v })}
                data={(facets.data?.models ?? []).map((v) => ({ value: v.value, label: `${v.value} · ${n(v.count)}` }))} />
              <Select size="xs" w={130} clearable placeholder="все" label="Категория" value={filters.category} onChange={(v) => filter({ category: v })}
                data={(facets.data?.categories ?? []).map((v) => ({ value: v.value, label: `${categoryLabel(v.value)} · ${n(v.count)}` }))} />
              <NumberInput size="xs" w={90} label="Глубина от" min={0} value={filters.minDepth ?? ''} onChange={(v) => filter({ minDepth: v === '' ? null : Number(v) })} />
              <Select size="xs" w={150} label="Порядок" allowDeselect={false} value={filters.sort} onChange={(v) => filter({ sort: v as HitFilters['sort'] })}
                data={[{ value: 'setpair', label: 'по парам наборов' }, { value: 'depth', label: 'глубже сначала' }, { value: 'number', label: 'по номеру' }]} />
            </Group>
            <ScrollArea style={{ flex: 1 }} type="auto">
              {nav.items.map((h, i) => {
                const header = filters.sort === 'setpair' && (i === 0 || nav.items[i - 1].setPairKey !== h.setPairKey);
                const pair = pairNames.get(h.setPairKey);
                return (
                  <Fragment key={h.number}>
                    {header && (
                      <UnstyledButton w="100%" px="xs" py={4} bg="var(--mantine-color-default-hover)"
                        onClick={() => setCollapsed((s) => { const next = new Set(s); if (next.has(h.setPairKey)) next.delete(h.setPairKey); else next.add(h.setPairKey); return next; })}>
                        <Group justify="space-between">
                          <Text size="xs" fw={700}>{collapsed.has(h.setPairKey) ? '▸' : '▾'} {pair ? `${pair.setAName} × ${pair.setBName}` : 'Наборы'}</Text>
                          {pair && <Text size="xs" fw={700}>{n(pair.count)}</Text>}
                        </Group>
                      </UnstyledButton>
                    )}
                    {!collapsed.has(h.setPairKey) && (
                      <UnstyledButton w="100%" px="xs" py={4} onClick={() => onNumber(h.number)} aria-label={`Коллизия ${h.number}`}
                        bg={h.number === number ? 'var(--mantine-primary-color-light)' : undefined}
                        style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
                        <Group gap={6} wrap="nowrap">
                          <Text size="xs" c="dimmed" w={52}>№ {h.number}</Text>
                          <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                            <Side hit={h} side="A" />
                            <Side hit={h} side="B" />
                          </Stack>
                          <Stack gap={0} align="flex-end">
                            <Text size="xs">{Math.round(h.depthMm)} мм</Text>
                            <Text size="xs" c="dimmed">{h.levelA ?? h.levelB ?? ''}</Text>
                          </Stack>
                        </Group>
                      </UnstyledButton>
                    )}
                  </Fragment>
                );
              })}
            </ScrollArea>
            <Group justify="space-between" px="xs" py={4} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
              <Text size="xs" c="dimmed">
                {nav.index >= 0 ? `${n((nav.page - 1) * PageSize + nav.index + 1)} из ${n(nav.hits?.total ?? 0)}` : `${n(nav.hits?.total ?? 0)} коллизий`}
                {checkInfo ? ` в «${checkInfo.groupAName} × ${checkInfo.groupBName}»` : ''}
              </Text>
              <Text size="xs" c="dimmed">↑ ↓ листать · Enter — свойства</Text>
            </Group>
          </Stack>

          <Box p="xs" style={{ flex: 1, minWidth: 480 }}>
            {current ? (
              <HitScene runId={current.runId} number={current.number} focus={focus}
                title={`№ ${current.number} · ${checkInfo ? `${checkInfo.groupAName} × ${checkInfo.groupBName} · ` : ''}глубина ${Math.round(current.depthMm)} мм`} />
            ) : (
              <Empty>Выберите коллизию в списке</Empty>
            )}
          </Box>
        </Group>
      )}
    </Stack>
  );
}
