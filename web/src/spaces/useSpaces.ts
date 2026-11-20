import { useApi } from '../api/queries';
import type { ModuleView } from '../api/types';
import { useAuth, type AuthState } from '../auth/AuthContext';
import { allSpaces } from './registry';
import type { Space, SpacePage } from './types';

/** Страницы пространства, которые видит пользователь: проектировщику — только свои, остальным — по роли. */
export function visiblePages(space: Space, auth: AuthState): SpacePage[] {
  return space.pages.filter((page) => {
    if (page.hidden?.(auth)) return false;
    if (!auth.isStaff) return page.designer === true;
    return !page.role || auth.can(page.role);
  });
}

/** Модули сервера и включён ли каждый (GET /api/modules). */
export function useModules(): ModuleView[] {
  return useApi<ModuleView[]>(['modules'], '/api/modules').data ?? [];
}

/** Пространства, которые видит пользователь: модуль включён на сервере и в пространстве есть видимые ему страницы. */
export function useSpaces(): { spaces: Space[]; loading: boolean } {
  const auth = useAuth();
  const query = useApi<ModuleView[]>(['modules'], auth.user ? '/api/modules' : null);
  const enabled = new Set((query.data ?? []).filter((m) => m.enabled).map((m) => m.name));
  const spaces = allSpaces.filter((space) => (!space.module || enabled.has(space.module)) && visiblePages(space, auth).length > 0);
  return { spaces, loading: query.isLoading };
}
