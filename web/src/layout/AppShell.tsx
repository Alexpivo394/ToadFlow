import { NavLink as RouterLink, Outlet, useLocation } from 'react-router';
import { ActionIcon, AppShell as Shell, Box, Burger, Group, NavLink, Stack, Text, Tooltip, useMantineColorScheme } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { IconCalendarTime, IconHistory, IconLayoutDashboard, IconLogout, IconMoon, IconSettings, IconSun } from '@tabler/icons-react';
import { useAuth } from '../auth/AuthContext';
import { Brand } from '../components/ui';
import { t } from '../i18n/ru';
import { useLiveUpdates } from '../live/useLiveUpdates';

const settingsLinks = [
  { to: '/settings/agents', label: t.nav.agents },
  { to: '/settings/sources', label: t.nav.sources },
  { to: '/settings/presets', label: t.nav.presets },
  { to: '/settings/loi', label: t.nav.loi },
  { to: '/settings/users', label: t.nav.users, admin: true },
  { to: '/settings/general', label: t.nav.general },
];

export function AppShell() {
  const { user, logout, can } = useAuth();
  const [opened, { toggle, close }] = useDisclosure();
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const location = useLocation();
  useLiveUpdates(user !== null);

  const inSettings = location.pathname.startsWith('/settings');

  return (
    <Shell navbar={{ width: 220, breakpoint: 'sm', collapsed: { mobile: !opened } }} header={{ height: { base: 52, sm: 0 } }} padding="md">
      <Shell.Header hiddenFrom="sm">
        <Group h="100%" px="md">
          <Burger opened={opened} onClick={toggle} size="sm" />
          <Brand><Text fw={700}>{t.appName}</Text></Brand>
        </Group>
      </Shell.Header>

      <Shell.Navbar p="sm">
        <Stack gap={4} h="100%">
          <Box px="sm" py="xs" visibleFrom="sm">
            <Brand>
              <Text fw={700} size="lg">{t.appName}</Text>
            </Brand>
          </Box>
          <NavLink component={RouterLink} to="/" end label={t.nav.dashboard} leftSection={<IconLayoutDashboard size={18} />} onClick={close} />
          <NavLink component={RouterLink} to="/schedules" label={t.nav.schedules} leftSection={<IconCalendarTime size={18} />} onClick={close} />
          <NavLink component={RouterLink} to="/history" label={t.nav.history} leftSection={<IconHistory size={18} />} onClick={close} />
          <NavLink label={t.nav.settings} leftSection={<IconSettings size={18} />} defaultOpened={inSettings} active={inSettings} childrenOffset={28}>
            {settingsLinks
              .filter((link) => !link.admin || can('Admin'))
              .map((link) => (
                <NavLink key={link.to} component={RouterLink} to={link.to} label={link.label} onClick={close} />
              ))}
          </NavLink>

          <Box mt="auto" pt="sm" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
            <Group justify="space-between" wrap="nowrap" px="sm">
              <Box style={{ minWidth: 0 }}>
                <Text fw={600} size="sm" truncate>
                  {user?.displayName}
                </Text>
                <Text size="xs" c="dimmed">
                  {user ? t.role[user.role] : ''}
                </Text>
              </Box>
              <Group gap={4} wrap="nowrap">
                <Tooltip label={t.common.themeToggle}>
                  <ActionIcon variant="subtle" onClick={toggleColorScheme} aria-label={t.common.themeToggle}>
                    {colorScheme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t.auth.logout}>
                  <ActionIcon variant="subtle" color="red" onClick={logout} aria-label={t.auth.logout}>
                    <IconLogout size={18} />
                  </ActionIcon>
                </Tooltip>
              </Group>
            </Group>
          </Box>
        </Stack>
      </Shell.Navbar>

      <Shell.Main>
        <Outlet />
      </Shell.Main>
    </Shell>
  );
}
