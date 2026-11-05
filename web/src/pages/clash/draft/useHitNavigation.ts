import { useEffect, useMemo, useRef, useState } from 'react';
import { useQueries } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import type { CheckCount, DraftHitView, HitPage } from '../../../api/types';

export const PageSize = 50;

export interface HitFilters {
  level: string | null;
  model: string | null;
  category: string | null;
  minDepth: number | null;
  sort: 'setpair' | 'depth' | 'number';
}

export const defaultFilters: HitFilters = { level: null, model: null, category: null, minDepth: null, sort: 'setpair' };

export function hitsQuery(check: string, filters: HitFilters, page: number) {
  const query = new URLSearchParams({ check, page: String(page), pageSize: String(PageSize), sort: filters.sort });
  if (filters.level) query.set('level', filters.level);
  if (filters.model) query.set('model', filters.model);
  if (filters.category) query.set('category', filters.category);
  if (filters.minDepth != null) query.set('minDepth', String(filters.minDepth));
  return query.toString();
}

interface Range { first: number; last: number }

/**
 * Коллизии проверки списком из подряд загруженных страниц `first…last`: прокрутка к краю (или «Показать ещё») добавляет
 * соседнюю страницу, а не заменяет показанную. Стрелки листают сквозь страницы, после последней — первая коллизия
 * следующей непустой проверки, перед первой — последняя предыдущей. Выбранная коллизия задаётся номером: он живёт в адресе.
 */
export function useHitNavigation({ partId, checks, check, number, filters, onCheck, onNumber }: {
  partId: string;
  checks: CheckCount[];
  check: string | null;
  number: number | null;
  filters: HitFilters;
  onCheck: (key: string) => void;
  onNumber: (number: number | null) => void;
}) {
  const [range, setRange] = useState<Range>({ first: 1, last: 1 });
  const [pending, setPending] = useState<{ where: 'first' | 'last'; check: string; page: number } | null>(null);
  const pageNumbers = check ? Array.from({ length: range.last - range.first + 1 }, (_, i) => range.first + i) : [];
  // Ключ — как у одной страницы раньше: живые обновления сущности `clash-parts` обновляют все загруженные страницы.
  const { loaded, fetching } = useQueries({
    queries: pageNumbers.map((page) => {
      const query = hitsQuery(check!, filters, page);
      return {
        queryKey: ['clash-parts', partId, 'hits', query],
        queryFn: async ({ signal }: { signal: AbortSignal }) =>
          ((await apiFetch<HitPage>(`/api/clash/parts/${partId}/draft/hits?${query}`, { signal })) ?? null) as HitPage,
      };
    }),
    combine: (results) => ({ loaded: results.map((r) => r.data), fetching: results.some((r) => r.isFetching) }),
  });
  const hits = loaded.find((d) => d != null) ?? undefined;
  const items = useMemo(() => loaded.flatMap((d) => d?.items ?? []), [loaded]);
  const pages = Math.max(1, Math.ceil((hits?.total ?? 0) / PageSize));

  // Новая проверка или фильтр — первая страница; при переходе стрелкой назад — последняя страница предыдущей проверки.
  const landing = useRef<number | null>(null);
  useEffect(() => {
    const page = landing.current ?? 1;
    setRange({ first: page, last: page });
    landing.current = null;
  }, [check, JSON.stringify(filters)]); // eslint-disable-line react-hooks/exhaustive-deps

  // Страница пришла после перехода стрелкой: выбрать её первую или последнюю коллизию. Ждём именно целевую проверку и
  // страницу — иначе выбор сработал бы на старой странице раньше, чем адрес переключится на новую проверку.
  const target = pending && pending.check === check && pending.page >= range.first && pending.page <= range.last
    ? loaded[pending.page - range.first] : undefined;
  useEffect(() => {
    if (!pending || !target || fetching) return;
    if (target.items.length > 0) onNumber(pending.where === 'first' ? target.items[0].number : target.items[target.items.length - 1].number);
    setPending(null);
  }, [pending, target, fetching, onNumber]);

  // Ничего не выбрано — первая коллизия проверки.
  useEffect(() => {
    if (number == null && !pending && items.length > 0) onNumber(items[0].number);
  }, [number, pending, items, onNumber]);

  const index = items.findIndex((h) => h.number === number);
  const current: DraftHitView | undefined = index >= 0 ? items[index] : undefined;
  const nonEmpty = checks.filter((c) => c.count > 0);
  const position = nonEmpty.findIndex((c) => c.key === check);
  const hasMore = range.last < pages;
  const hasPrevious = range.first > 1;

  /** Догрузить страницу после показанных: прокрутка к концу списка или «Показать ещё». */
  const loadMore = () => {
    if (!fetching && hasMore) setRange((r) => ({ ...r, last: r.last + 1 }));
  };
  /** Догрузить страницу перед показанными: список открыт с середины (стрелка назад из следующей проверки). */
  const loadPrevious = () => {
    if (!fetching && hasPrevious) setRange((r) => ({ ...r, first: r.first - 1 }));
  };

  const next = () => {
    if (index >= 0 && index + 1 < items.length) return onNumber(items[index + 1].number);
    if (hasMore && check) {
      setRange((r) => ({ ...r, last: r.last + 1 }));
      setPending({ where: 'first', check, page: range.last + 1 });
      return;
    }
    const following = nonEmpty[position + 1];
    if (following) {
      onCheck(following.key);
      setPending({ where: 'first', check: following.key, page: 1 });
    }
  };

  const prev = () => {
    if (index > 0) return onNumber(items[index - 1].number);
    if (hasPrevious && check) {
      setRange((r) => ({ ...r, first: r.first - 1 }));
      setPending({ where: 'last', check, page: range.first - 1 });
      return;
    }
    const preceding = position > 0 ? nonEmpty[position - 1] : undefined;
    if (preceding) {
      const last = Math.max(1, Math.ceil(preceding.count / PageSize));
      onCheck(preceding.key);
      landing.current = last;
      setPending({ where: 'last', check: preceding.key, page: last });
    }
  };

  /** Следующая коллизия после выбранной — её сцену стоит загрузить заранее. */
  const upcoming = index >= 0 && index + 1 < items.length ? items[index + 1] : undefined;
  /** Порядковый номер выбранной коллизии в проверке с единицы (с учётом страниц перед показанными), -1 — не выбрана. */
  const ordinal = index >= 0 ? (range.first - 1) * PageSize + index + 1 : -1;

  return { hits, items, pages, fetching, current, index, ordinal, next, prev, upcoming, loadMore, loadPrevious, hasMore, hasPrevious };
}
