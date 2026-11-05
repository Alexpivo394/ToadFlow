import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { Alert, Badge, Box, Button, Group, NavLink, NumberInput, ScrollArea, Select, Stack, Text, UnstyledButton } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useApi } from '../../../api/queries';
import type { CheckCount, CollisionFacets, DraftFacets, DraftHitView, DraftRunSummary, DraftSummary, SetPairCount } from '../../../api/types';
import { ListFilters } from '../../../components/collisions/ListFilters';
import { useListParams, type ListParams } from '../../../components/collisions/useListParams';
import { Empty } from '../../../components/ui';
import { categoryLabel } from '../../../lib/categories';
import type { ClashConfig } from '../../../lib/clashConfig';
import { DraftRunPanel } from '../DraftRunPanel';
import { HitScene, prefetchHit } from '../viewer/HitScene';
import { useListKeyboard } from '../../../hooks/useListKeyboard';
import { type LabeledEdit, SetPairMenu } from './SetPairMenu';
import { type HitFilters, useHitNavigation } from './useHitNavigation';

const n = (value: number) => value.toLocaleString('ru-RU');

const draftSorts = [{ value: 'setpair', label: 'По парам наборов' }, { value: 'depth', label: 'По глубине' }, { value: 'number', label: 'По номеру' }];
const draftGroups = [{ value: 'setpair', label: 'По парам наборов' }, { value: 'none', label: 'Без группировки' }];

/** Проверки в порядке матрицы, сгруппированные по группе строки; пустые — серым, чтобы было видно, что их проверили. */
export function ChecksList({ checks, selected, onSelect }: { checks: CheckCount[]; selected: string | null; onSelect: (key: string) => void }) {
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
export function DraftTab({ partId, readOnly, dirty, onSave, config, onEdit, onOpenSet }: {
  partId: string;
  readOnly: boolean;
  dirty: boolean;
  onSave: () => Promise<void>;
  /** Черновик с несохранёнными правками — для меню «⋯» у пары наборов. */
  config?: ClashConfig;
  onEdit?: LabeledEdit;
  onOpenSet?: (setId: string) => void;
}) {
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [list, setList] = useListParams({ sort: 'setpair', group: 'setpair' });
  const [minDepth, setMinDepth] = useState<number | null>(null);
  const [more, setMore] = useState(false);
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
  const filters: HitFilters = {
    level: list.level ?? null, model: list.model ?? null, category: list.category ?? null, minDepth,
    sort: list.sort === 'depth' || list.sort === 'number' ? list.sort : 'setpair',
  };
  // Группы по парам наборов идут подряд только при сортировке по ним: выбор группы ставит её, другая сортировка снимает группы.
  const grouped = list.group === 'setpair' && filters.sort === 'setpair';
  const change = (patch: Partial<ListParams>) => {
    if ('q' in patch) {
      const value = Number(patch.q);
      if (patch.q && Number.isInteger(value) && value > 0) onNumber(value);
      return;
    }
    if (patch.group === 'setpair') setList({ ...patch, sort: 'setpair' });
    else if (patch.sort && patch.sort !== 'setpair') setList({ ...patch, group: 'none' });
    else setList(patch);
  };
  const open = (target: number | string) => { if (typeof target === 'number') onNumber(target); };

  const nav = useHitNavigation({ partId, checks, check, number, filters, onCheck, onNumber });
  const facets = useApi<DraftFacets>(['clash-parts', partId, 'facets', check], check ? `/api/clash/parts/${partId}/draft/facets?check=${check}` : null);
  const setPairs = useApi<SetPairCount[]>(['clash-parts', partId, 'setpairs', check], check ? `/api/clash/parts/${partId}/draft/checks/${check}/setpairs` : null);
  const pairNames = new Map((setPairs.data ?? []).map((p) => [`${p.setA.replaceAll('-', '')}|${p.setB.replaceAll('-', '')}`, p]));
  // Пара, исключённая в черновике, но ещё с коллизиями в результате: прогон старше правки — группа бледнеет до нового прогона.
  const excluded = (pair: SetPairCount | undefined) => !!pair && !!config?.exclusions.some((e) =>
    (e.setA === pair.setA && e.setB === pair.setB) || (e.setA === pair.setB && e.setB === pair.setA));

  useEffect(() => {
    if (nav.upcoming) prefetchHit(queryClient, nav.upcoming.runId, nav.upcoming.number);
  }, [nav.upcoming, queryClient]);

  useListKeyboard({ onUp: () => nav.prev(), onDown: () => nav.next(), onEnter: () => setFocus((f) => (f === 'A' ? 'B' : 'A')) });

  const current = nav.current;
  // Номер из поиска может быть не на этой странице и не в этой проверке: сцена берёт его из последнего прогона черновика.
  const run = useApi<DraftRunSummary | null>(['clash-parts', partId, 'run'], `/api/clash/parts/${partId}/draft/run`);
  const runActive = run.data?.state === 'Queued' || run.data?.state === 'Running';
  const shown = current ?? (number != null && run.data ? { runId: run.data.runId, number } : null);
  const facetLists: CollisionFacets = { pairs: [], levels: facets.data?.levels ?? [], models: [], categories: [] };
  const checkInfo = checks.find((c) => c.key === check);

  return (
    <Stack gap="sm">
      <DraftRunPanel partId={partId} readOnly={readOnly} dirty={dirty} onSave={onSave} />
      {summary.data?.legacy && <Alert color="orange">Результат получен до появления групп проверки — прогоните черновик заново, чтобы разложить коллизии по проверкам.</Alert>}
      {summary.data && summary.data.total === 0 && !summary.data.legacy ? (
        // Пока идёт прогон, «нажмите Прогнать» вводит в заблуждение: кнопка выключена, результат ещё будет (W-3).
        <Empty>{runActive ? 'Идёт прогон…' : 'Коллизий пока нет — нажмите «Прогнать»'}</Empty>
      ) : (
        <Group align="stretch" gap={0} wrap="nowrap" h="calc(100vh - 290px)" mih={520}
          style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
          <Box w={{ base: 200, xl: 250 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
            <Text size="xs" fw={700} c="dimmed" tt="uppercase" px="sm" pt="sm">Проверки</Text>
            <ChecksList checks={checks} selected={check} onSelect={onCheck} />
          </Box>

          <Stack gap={0} w={{ base: 370, xl: 430 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
            <Stack gap={6} p="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
              <ListFilters params={list} facets={facetLists} fields={['q', 'level', 'sort', 'group', 'more']} sorts={draftSorts}
                groups={draftGroups} onChange={change} onOpen={open} onMore={() => setMore((m) => !m)} />
              {more && (
                <Group gap="xs">
                  <Select size="xs" w={150} clearable searchable placeholder="Модель" aria-label="Модель" value={list.model ?? null}
                    onChange={(v) => setList({ model: v ?? undefined })}
                    data={(facets.data?.models ?? []).map((v) => ({ value: v.value, label: `${v.value} — ${n(v.count)}` }))} />
                  <Select size="xs" w={150} clearable searchable placeholder="Категория" aria-label="Категория" value={list.category ?? null}
                    onChange={(v) => setList({ category: v ?? undefined })}
                    data={(facets.data?.categories ?? []).map((v) => ({ value: v.value, label: `${categoryLabel(v.value)} — ${n(v.count)}` }))} />
                  <NumberInput size="xs" w={110} placeholder="Глубина от, мм" aria-label="Глубина от" min={0} value={minDepth ?? ''}
                    onChange={(v) => setMinDepth(v === '' ? null : Number(v))} />
                </Group>
              )}
            </Stack>
            <ScrollArea style={{ flex: 1 }} type="auto" onBottomReached={nav.loadMore}>
              {nav.hasPrevious && (
                <Button variant="subtle" size="xs" fullWidth loading={nav.fetching} onClick={nav.loadPrevious}>Показать предыдущие</Button>
              )}
              {nav.items.map((h, i) => {
                const header = grouped && (i === 0 || nav.items[i - 1].setPairKey !== h.setPairKey);
                const pair = pairNames.get(h.setPairKey);
                return (
                  <Fragment key={h.number}>
                    {header && (
                      <Group gap={4} wrap="nowrap" px="xs" py={4} bg="var(--mantine-color-default-hover)" style={{ opacity: excluded(pair) ? 0.5 : 1 }}>
                        <UnstyledButton style={{ flex: 1, minWidth: 0 }}
                          onClick={() => setCollapsed((s) => { const next = new Set(s); if (next.has(h.setPairKey)) next.delete(h.setPairKey); else next.add(h.setPairKey); return next; })}>
                          <Group justify="space-between" wrap="nowrap">
                            <Text size="xs" fw={700} truncate>{collapsed.has(h.setPairKey) ? '▸' : '▾'} {pair ? `${pair.setAName} × ${pair.setBName}` : 'Наборы'}</Text>
                            <Group gap={6} wrap="nowrap">
                              {excluded(pair) && <Badge size="xs" color="gray" variant="light">будет исключена</Badge>}
                              {pair && <Text size="xs" fw={700}>{n(pair.count)}</Text>}
                            </Group>
                          </Group>
                        </UnstyledButton>
                        {pair && config && onEdit && onOpenSet && !readOnly && <SetPairMenu config={config} pair={pair} onEdit={onEdit} onOpenSet={onOpenSet} />}
                      </Group>
                    )}
                    {!(grouped && collapsed.has(h.setPairKey)) && (
                      <UnstyledButton w="100%" px="xs" py={4} onClick={() => onNumber(h.number)} aria-label={`Коллизия ${h.number}`}
                        bg={h.number === number ? 'var(--mantine-primary-color-light)' : undefined}
                        style={{ borderBottom: '1px solid var(--mantine-color-default-border)', opacity: excluded(pair) ? 0.5 : 1 }}>
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
              {nav.hasMore && (
                // Прокрутка к концу догружает сама; кнопка — на случай, когда список короче окна и прокручивать нечего.
                <Button variant="subtle" size="xs" fullWidth loading={nav.fetching} onClick={nav.loadMore}>
                  Показать ещё (показано {n(nav.items.length)} из {n(nav.hits?.total ?? 0)})
                </Button>
              )}
            </ScrollArea>
            <Group justify="space-between" px="xs" py={4} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
              <Text size="xs" c="dimmed">
                {nav.ordinal > 0 ? `${n(nav.ordinal)} из ${n(nav.hits?.total ?? 0)}` : `${n(nav.hits?.total ?? 0)} коллизий`}
                {checkInfo ? ` в «${checkInfo.groupAName} × ${checkInfo.groupBName}»` : ''}
              </Text>
              <Text size="xs" c="dimmed">↑ ↓ листать · Enter — свойства</Text>
            </Group>
          </Stack>

          <Box p="xs" style={{ flex: 1, minWidth: 480 }}>
            {shown ? (
              <HitScene runId={shown.runId} number={shown.number} focus={focus}
                title={current
                  ? `№ ${current.number} · ${checkInfo ? `${checkInfo.groupAName} × ${checkInfo.groupBName} · ` : ''}глубина ${Math.round(current.depthMm)} мм`
                  : `№ ${shown.number}`} />
            ) : (
              <Empty>Выберите коллизию в списке</Empty>
            )}
          </Box>
        </Group>
      )}
    </Stack>
  );
}
