import { NavLink as RouterLink, useLocation } from 'react-router';
import { ActionIcon, Box, Stack, Text, Tooltip, UnstyledButton, useMantineColorScheme } from '@mantine/core';
import { IconLayoutDashboard, IconLogout, IconMoon, IconSun, IconUserCircle, type Icon } from '@tabler/icons-react';
import { useAuth } from '../auth/AuthContext';
import { t } from '../i18n/ru';
import { visiblePages } from '../spaces/useSpaces';
import type { Space } from '../spaces/types';

/** Ширина полосы пространств. */
export const railWidth = 72;

function RailLink({ to, label, short, icon: IconComponent, active, onClick, tooltip }: {
  to: string; label: string; short: string; icon: Icon; active: boolean; onClick: () => void; tooltip?: string;
}) {
  return (
    <Tooltip label={tooltip ?? label} position="right" withinPortal>
      <UnstyledButton component={RouterLink} to={to} aria-label={label} onClick={onClick} py={6} w="100%"
        style={{ borderRadius: 'var(--mantine-radius-sm)', background: active ? 'var(--mantine-primary-color-light)' : undefined }}>
        <Stack gap={2} align="center">
          <IconComponent size={22} stroke={1.6} color={active ? 'var(--mantine-primary-color-filled)' : undefined} />
          <Text size="10px" lh={1.1} ta="center" c={active ? undefined : 'dimmed'} fw={active ? 600 : 400}>
            {short}
          </Text>
        </Stack>
      </UnstyledButton>
    </Tooltip>
  );
}

/**
 * Узкая полоса слева: «Обзор» (не проектировщику), пространства включённых модулей, «Ферма» и «Администрирование»; внизу —
 * профиль, тема, выход. Щелчок по пространству открывает его первую доступную пользователю страницу.
 */
export function Rail({ spaces, current, onNavigate }: { spaces: Space[]; current: Space | null; onNavigate: () => void }) {
  const auth = useAuth();
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const { pathname } = useLocation();

  return (
    <Stack w={railWidth} h="100%" gap={4} p={6} justify="space-between" style={{ borderRight: '1px solid var(--mantine-color-default-border)' }}>
      <Stack gap={4} role="group" aria-label="Пространства" style={{ flex: 1, minHeight: 0 }}>
        {auth.isStaff && (
          <RailLink to="/" label={t.nav.overview} short={t.nav.shortOverview} icon={IconLayoutDashboard} active={pathname === '/'} onClick={onNavigate} />
        )}
        {spaces.map((space) => (
          <RailLink key={space.id} to={visiblePages(space, auth)[0].to} label={space.label} short={space.short} icon={space.icon}
            active={current?.id === space.id} onClick={onNavigate} />
        ))}
        <Box mt="auto" />
        <RailLink to="/profile" label={t.nav.profile} short={t.nav.profile} icon={IconUserCircle} active={pathname === '/profile'} onClick={onNavigate}
          tooltip={auth.user ? `${auth.user.displayName} · ${t.role[auth.user.role]}` : undefined} />
      </Stack>
      <Stack gap={4} align="center">
        <Tooltip label={t.common.themeToggle} position="right">
          <ActionIcon variant="subtle" onClick={toggleColorScheme} aria-label={t.common.themeToggle}>
            {colorScheme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t.auth.logout} position="right">
          <ActionIcon variant="subtle" color="red" onClick={auth.logout} aria-label={t.auth.logout}>
            <IconLogout size={18} />
          </ActionIcon>
        </Tooltip>
      </Stack>
    </Stack>
  );
}
