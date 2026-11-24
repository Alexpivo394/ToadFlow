import { Suspense } from 'react';
import { Outlet, useLocation } from 'react-router';
import { AppShell as Shell, Burger, Center, Group, Loader, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { useAuth } from '../auth/AuthContext';
import { Brand } from '../components/ui';
import { t } from '../i18n/ru';
import { useLiveUpdates } from '../live/useLiveUpdates';
import { NotificationBell } from '../notifications/NotificationBell';
import { useNotificationsLive } from '../notifications/useNotificationsLive';
import { CommandPalette, SearchButton } from '../search/CommandPalette';
import { usePageViews } from '../pages/usage/usePageViews';
import type { Space } from '../spaces/types';
import { Rail, railWidth } from './Rail';
import { menuWidth, SpaceMenu } from './SpaceMenu';

/** Пространство, в котором находится адрес: /{id} или /{id}/…; «Обзор» и профиль — вне пространств. */
export function spaceOf(spaces: Space[], pathname: string): Space | null {
  return spaces.find((space) => pathname === `/${space.id}` || pathname.startsWith(`/${space.id}/`)) ?? null;
}

/**
 * Оболочка: полоса пространств и меню выбранного пространства слева, страница справа. У «Обзора» меню нет — страница во всю ширину.
 * На узком экране полоса и меню прячутся под одну кнопку.
 */
export function AppShell({ spaces }: { spaces: Space[] }) {
  const { user, isStaff } = useAuth();
  const [opened, { toggle, close }] = useDisclosure();
  const { pathname } = useLocation();
  // Хаб рассылает события всех модулей — проектировщику он закрыт; его экраны обновляются при открытии и после действий.
  useLiveUpdates(user !== null && isStaff);
  // Свои уведомления — любой роли: личный хаб.
  useNotificationsLive(user !== null);
  // Переходы по страницам — для экрана «Сервис» аналитики (если модуль включён).
  usePageViews(spaces);

  const current = spaceOf(spaces, pathname);

  return (
    <Shell navbar={{ width: railWidth + (current ? menuWidth : 0), breakpoint: 'sm', collapsed: { mobile: !opened } }}
      header={{ height: { base: 52, sm: 0 } }} padding="md">
      <Shell.Header hiddenFrom="sm">
        <Group h="100%" px="md">
          <Burger opened={opened} onClick={toggle} size="sm" aria-label={t.nav.menu} />
          <Brand><Text fw={700}>{t.appName}</Text></Brand>
          <Group ml="auto" gap={4}><SearchButton /><NotificationBell position="bottom-end" /></Group>
        </Group>
      </Shell.Header>

      <Shell.Navbar>
        <Group gap={0} h="100%" wrap="nowrap" align="stretch">
          <Rail spaces={spaces} current={current} onNavigate={close} />
          {current && <SpaceMenu space={current} onNavigate={close} />}
        </Group>
      </Shell.Navbar>

      <CommandPalette />
      <Shell.Main>
        {/* Страницы грузятся своими чанками: пока чанк идёт, меню остаётся, на месте страницы — загрузка. */}
        <Suspense fallback={<Center py="xl"><Loader /></Center>}>
          <Outlet />
        </Suspense>
      </Shell.Main>
    </Shell>
  );
}
