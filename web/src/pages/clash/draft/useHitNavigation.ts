import { useEffect, useMemo, useRef, useState } from 'react';
import { useApi } from '../../../api/queries';
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

/**
 * Листание коллизий проверки стрелками: сквозь страницы, после последней — первая коллизия следующей непустой проверки,
 * перед первой — последняя предыдущей. Выбранная коллизия задаётся номером: он живёт в адресе страницы.
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
  const [page, setPage] = useState(1);
  const [pending, setPending] = useState<{ where: 'first' | 'last'; check: string; page: number } | null>(null);
  const query = check ? hitsQuery(check, filters, page) : null;
  const hits = useApi<HitPage>(['clash-parts', partId, 'hits', query], query ? `/api/clash/parts/${partId}/draft/hits?${query}` : null);
  const items = useMemo(() => hits.data?.items ?? [], [hits.data]);
  const pages = Math.max(1, Math.ceil((hits.data?.total ?? 0) / PageSize));

  // Новая проверка или фильтр — первая страница; при переходе стрелкой назад — последняя страница предыдущей проверки.
  const landing = useRef<number | null>(null);
  useEffect(() => {
    setPage(landing.current ?? 1);
    landing.current = null;
  }, [check, JSON.stringify(filters)]); // eslint-disable-line react-hooks/exhaustive-deps

  // Страница пришла после перехода стрелкой: выбрать её первую или последнюю коллизию. Ждём именно целевую проверку и
  // страницу — иначе выбор сработал бы на старой странице раньше, чем адрес переключится на новую проверку.
  useEffect(() => {
    if (!pending || pending.check !== check || pending.page !== page || !hits.data || hits.isFetching) return;
    if (items.length > 0) onNumber(pending.where === 'first' ? items[0].number : items[items.length - 1].number);
    setPending(null);
  }, [pending, check, page, hits.data, hits.isFetching, items, onNumber]);

  // Ничего не выбрано — первая коллизия проверки.
  useEffect(() => {
    if (number == null && !pending && items.length > 0) onNumber(items[0].number);
  }, [number, pending, items, onNumber]);

  const index = items.findIndex((h) => h.number === number);
  const current: DraftHitView | undefined = index >= 0 ? items[index] : undefined;
  const nonEmpty = checks.filter((c) => c.count > 0);
  const position = nonEmpty.findIndex((c) => c.key === check);

  const next = () => {
    if (index >= 0 && index + 1 < items.length) return onNumber(items[index + 1].number);
    if (page < pages && check) {
      setPage(page + 1);
      setPending({ where: 'first', check, page: page + 1 });
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
    if (page > 1 && check) {
      setPage(page - 1);
      setPending({ where: 'last', check, page: page - 1 });
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

  return { hits: hits.data, items, page, pages, setPage, current, index, next, prev, upcoming };
}
