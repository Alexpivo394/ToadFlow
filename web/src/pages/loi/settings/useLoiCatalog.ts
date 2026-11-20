import { useMemo } from 'react';
import { useApi } from '../../../api/queries';
import type { LoiCatalogOverview, LoiCatalogView } from '../../../api/types';

export interface CategoryOption {
  value: string;
  label: string;
  isModel: boolean;
}

/**
 * Каталог для редактора правил: новейший из снятых Revit на агентах. Категории без подписи (их нет в каталоге этой версии
 * Revit или каталог ещё не снят) показываются своими ключами — правило от этого не портится.
 */
export function useLoiCatalog() {
  const overview = useApi<LoiCatalogOverview>(['loi-catalog'], '/api/loi/catalog');
  const year = overview.data?.catalogs[0]?.revitYear ?? null;
  const catalog = useApi<LoiCatalogView>(['loi-catalog', year], year ? `/api/loi/catalog/${year}` : null);

  return useMemo(() => {
    const data = catalog.data?.catalog;
    const categories: CategoryOption[] = (data?.categories ?? [])
      .map((c) => ({ value: c.key, label: c.name, isModel: c.isModel }))
      .sort((a, b) => a.label.localeCompare(b.label, 'ru'));
    const names = new Map(categories.map((c) => [c.value, c.label]));
    const parameters = data?.parameters ?? [];
    const known = new Set(parameters.map((p) => p.toLocaleLowerCase('ru')));

    return {
      overview: overview.data,
      year,
      loaded: Boolean(data),
      categories,
      parameters,
      categoryName: (key: string) => names.get(key) ?? key,
      /** Имя похоже на опечатку: ФОП у Revit на агенте подключён, а такого параметра в каталоге нет. */
      isSuspicious: (name: string) => Boolean(data?.hasSharedParameterFile) && name.trim().length > 0 && !known.has(name.trim().toLocaleLowerCase('ru')),
    };
  }, [overview.data, catalog.data, year]);
}

export type LoiCatalog = ReturnType<typeof useLoiCatalog>;
