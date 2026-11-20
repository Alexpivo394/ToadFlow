import type { ReactNode } from 'react';
import type { Icon } from '@tabler/icons-react';
import type { AuthState } from '../auth/AuthContext';

export type SpaceId = 'export' | 'loi' | 'clash' | 'farm' | 'admin';

/** Страница меню пространства. */
export interface SpacePage {
  to: string;
  label: string;
  /** Активна только при точном совпадении адреса (страница — корень других). */
  end?: boolean;
  /** С какой роли видна; нет — Наблюдатель и старше. */
  role?: 'Operator' | 'Admin';
  /** Видна проектировщику. */
  designer?: boolean;
  /** Скрыть для этого пользователя (например, «Мои» без разделов). */
  hidden?: (auth: AuthState) => boolean;
  /** «setup» — настройка, рисуется под разделителем. */
  section: 'work' | 'setup';
}

/**
 * Пространство в полосе слева: модуль, «Ферма» или «Администрирование». Новый модуль описывает себя одним таким манифестом —
 * оболочка собирает из манифестов полосу, меню и маршруты.
 */
export interface Space {
  id: SpaceId;
  /** Имя модуля сервера; нет — пространство есть всегда. */
  module?: string;
  label: string;
  /** Подпись под значком в полосе. */
  short: string;
  icon: Icon;
  pages: SpacePage[];
  /** <Route>-элементы пространства, пути относительно /{id}. */
  routes: () => ReactNode;
}
