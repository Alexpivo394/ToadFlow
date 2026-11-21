import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import { Checkbox, Group, Pagination, Select, SimpleGrid, Stack, Text, TextInput } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { QueryError } from '../../components/QueryState';
import { Empty } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type CardState, type FacetValue, type FamilyFacets, type FamilyPage } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { FamilyTile } from './FamilyTile';

const pageSize = 40;
const searchDelayMs = 300;

/** Параметры поиска из адреса — то, что уходит серверу. */
const searchKeys = ['q', 'section', 'category', 'year', 'placement', 'state', 'unreviewed', 'page'] as const;

const options = (values: FacetValue[] | undefined, label: (value: string) => string = (v) => v) =>
  (values ?? []).map((f) => ({ value: f.value, label: `${label(f.value)} (${f.count})` }));

/**
 * Каталог семейств: строка поиска (запрос — через 300 мс после остановки ввода), фильтры из фасетов, сетка плиток, карточка
 * в боковой панели. Всё состояние — в адресе: ссылка открывает тот же поиск и ту же карточку.
 */
export function CatalogPage() {
  const auth = useAuth();
  const designer = !auth.isStaff;
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const [text, setText] = useState(q);

  const update = (patch: Record<string, string | null>) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      for (const [key, value] of Object.entries(patch)) {
        if (value === null || value === '') next.delete(key);
        else next.set(key, value);
      }
      if (!('page' in patch)) next.delete('page');
      return next;
    }, { replace: true });

  useEffect(() => {
    if (text.trim() === q) return;
    const timer = setTimeout(() => update({ q: text.trim() }), searchDelayMs);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- ждём только остановки ввода
  }, [text]);

  const search = new URLSearchParams();
  for (const key of searchKeys) {
    const value = params.get(key);
    if (value) search.set(key, value);
  }
  search.set('pageSize', String(pageSize));
  const visibility = new URLSearchParams();
  for (const key of ['state', 'unreviewed'] as const) if (params.get(key)) visibility.set(key, params.get(key)!);

  const result = useApi<FamilyPage>([familiesKey, 'search', search.toString()], `/api/families/search?${search}`, { keepPrevious: true });
  const facets = useApi<FamilyFacets>([familiesKey, 'facets', visibility.toString()], `/api/families/facets?${visibility}`);
  const unreviewed = params.get('unreviewed') === 'true';
  const page = Number(params.get('page') ?? '1') || 1;
  const pages = Math.max(1, Math.ceil((result.data?.total ?? 0) / pageSize));
  const filtering = searchKeys.some((key) => key !== 'page' && params.get(key));

  return (
    <Stack gap="md">
      <TextInput aria-label="Поиск семейств" placeholder={t.families.searchPlaceholder} leftSection={<IconSearch size={16} />} size="md" value={text}
        onChange={(e) => setText(e.currentTarget.value)} />
      <Group gap="sm" align="flex-end">
        <Select w={200} label={t.families.section} clearable data={options(facets.data?.sections)} value={params.get('section')}
          onChange={(value) => update({ section: value })} />
        <Select w={240} label={t.families.category} clearable searchable data={options(facets.data?.categories)} value={params.get('category')}
          onChange={(value) => update({ category: value })} />
        <Select w={160} label={t.families.year} clearable data={options(facets.data?.years)} value={params.get('year')}
          onChange={(value) => update({ year: value })} />
        <Select w={220} label={t.families.placement} clearable data={options(facets.data?.placements, (v) => t.placement[v] ?? v)}
          value={params.get('placement')} onChange={(value) => update({ placement: value })} />
        {designer ? (
          <Checkbox label={t.families.showUnreviewed} checked={unreviewed} mb={8}
            onChange={(e) => update({ unreviewed: e.currentTarget.checked ? 'true' : null })} />
        ) : (
          <Select w={180} label={t.families.state} data={(['Approved', 'InReview', 'Archived'] as CardState[]).map((s) => ({ value: s, label: t.familyState[s] }))}
            value={params.get('state') ?? 'Approved'} allowDeselect={false} onChange={(value) => update({ state: value === 'Approved' ? null : value })} />
        )}
      </Group>
      <QueryError query={result} />
      {result.data && (
        <>
          <Text size="sm" c="dimmed">Найдено: {result.data.total}</Text>
          {result.data.items.length === 0 ? (
            <Empty>{filtering ? t.families.nothingFound : t.families.emptyLibrary}</Empty>
          ) : (
            <SimpleGrid cols={{ base: 2, sm: 3, md: 4, lg: 5, xl: 6 }} spacing="sm">
              {result.data.items.map((hit) => (
                <FamilyTile key={hit.id} hit={hit} onOpen={() => update({ card: hit.id, page: params.get('page') })} />
              ))}
            </SimpleGrid>
          )}
          {pages > 1 && <Pagination value={Math.min(page, pages)} total={pages} onChange={(value) => update({ page: value === 1 ? null : String(value) })} />}
        </>
      )}
      <FamilyCardDrawer cardId={params.get('card')} unreviewed={unreviewed} onClose={() => update({ card: null, page: params.get('page') })} />
    </Stack>
  );
}
