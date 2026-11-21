import { Link } from 'react-router';
import { Button, Group, Stack, Title } from '@mantine/core';
import { IconBooks, IconListCheck, IconPlus, IconTarget } from '@tabler/icons-react';
import { useAuth } from '../../auth/AuthContext';
import { t } from '../../i18n/ru';
import { useModules } from '../../spaces/useSpaces';
import { greeting } from '../overview/greeting';
import { TodayBlock } from './TodayBlock';

/** Главная проектировщика: приветствие, «Для вас» и быстрые переходы — найти семейство, подать заявку, мои заявки, мои коллизии. */
export function TodayPage() {
  const { user, disciplines } = useAuth();
  const modules = useModules();
  const on = (name: string) => modules.length === 0 || modules.some((m) => m.name === name && m.enabled);
  return (
    <Stack gap="md" maw={900}>
      <Title order={2}>{greeting(new Date().getHours())}{user?.displayName ? `, ${user.displayName}` : ''}</Title>
      <Title order={3}>{t.today.title}</Title>
      <TodayBlock />
      <Group gap="sm">
        {on('families') && (
          <>
            <Button component={Link} to="/families/catalog" variant="default" leftSection={<IconBooks size={16} />}>{t.today.findFamily}</Button>
            <Button component={Link} to="/families/requests/new" leftSection={<IconPlus size={16} />}>{t.today.newRequest}</Button>
            <Button component={Link} to="/families/requests" variant="default" leftSection={<IconListCheck size={16} />}>{t.today.myRequests}</Button>
          </>
        )}
        {on('clash') && disciplines.length > 0 && (
          <Button component={Link} to="/clash/my" variant="default" leftSection={<IconTarget size={16} />}>{t.today.myCollisions}</Button>
        )}
      </Group>
    </Stack>
  );
}
