import { NavLink as RouterLink } from 'react-router';
import { Divider, NavLink, Stack, Text } from '@mantine/core';
import { useAuth } from '../auth/AuthContext';
import { visiblePages } from '../spaces/useSpaces';
import type { Space, SpacePage } from '../spaces/types';

/** Ширина меню пространства. */
export const menuWidth = 200;

/** Меню выбранного пространства: рабочие страницы сверху, настройка — ниже под разделителем и только тем, кому она доступна. */
export function SpaceMenu({ space, onNavigate }: { space: Space; onNavigate: () => void }) {
  const auth = useAuth();
  const pages = visiblePages(space, auth);
  const work = pages.filter((page) => page.section === 'work');
  const setup = pages.filter((page) => page.section === 'setup');

  const link = (page: SpacePage) => (
    <NavLink key={page.to} component={RouterLink} to={page.to} end={page.end} label={page.label} onClick={onNavigate} />
  );

  return (
    <Stack w={menuWidth} gap={2} p="sm" role="group" aria-label={space.label} style={{ overflowY: 'auto' }}>
      <Text fw={700} size="xs" tt="uppercase" c="dimmed" px="sm" py={6}>
        {space.label}
      </Text>
      {work.map(link)}
      {setup.length > 0 && (
        <Stack gap={2} role="group" aria-label="Настройка">
          {work.length > 0 && <Divider my={6} />}
          {setup.map(link)}
        </Stack>
      )}
    </Stack>
  );
}
