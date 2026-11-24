import { useEffect, useRef } from 'react';
import { useLocation } from 'react-router';
import { apiFetch } from '../../api/client';
import { useModules } from '../../spaces/useSpaces';
import type { Space } from '../../spaces/types';

/** Куда пришёл человек: пространство и пункт меню, без параметров и вложенных адресов. */
export function pageOf(spaces: Space[], pathname: string): { space: string; page: string } {
  const space = spaces.find((s) => pathname === `/${s.id}` || pathname.startsWith(`/${s.id}/`));
  if (!space) {
    // Вне пространств — «Обзор», профиль и т. п.: первый сегмент адреса.
    const first = pathname.split('/').filter(Boolean)[0];
    return first ? { space: first, page: `/${first}` } : { space: 'overview', page: '/' };
  }
  const page = space.pages
    .map((p) => p.to)
    .filter((to) => pathname === to || pathname.startsWith(`${to}/`))
    .sort((a, b) => b.length - a.length)[0];
  return { space: space.id, page: page ?? `/${space.id}` };
}

/**
 * Сообщает серверу о переходе на страницу веба — для экрана «Сервис» аналитики. Только при включённом модуле; смена параметров
 * адреса и вкладок той же страницы — не переход. Ошибка запроса не мешает работе.
 */
export function usePageViews(spaces: Space[]) {
  const { pathname } = useLocation();
  const enabled = useModules().some((m) => m.name === 'usage' && m.enabled);
  const last = useRef<string | null>(null);

  useEffect(() => {
    if (!enabled) return;
    const view = pageOf(spaces, pathname);
    const key = `${view.space} ${view.page}`;
    if (key === last.current) return;
    last.current = key;
    apiFetch('/api/usage/pageview', { method: 'POST', json: view }).catch(() => undefined);
  }, [enabled, spaces, pathname]);
}
