import { useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { ActionIcon, Badge, Box, Button, Divider, Group, Indicator, Popover, ScrollArea, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconBell } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../api/client';
import { useApi } from '../api/queries';
import { t } from '../i18n/ru';
import { notificationsKey, type NotificationCount, type NotificationItem } from './api';

const titlePrefix = /^\(\d+\+?\) /;

/** «5 мин назад», «вчера в 14:20», «12.11 в 09:00». */
export function ago(iso: string, now = new Date()): string {
  const at = new Date(iso);
  const minutes = Math.floor((now.getTime() - at.getTime()) / 60_000);
  const time = at.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  if (minutes < 1) return 'только что';
  if (minutes < 60) return `${minutes} мин назад`;
  const sameDay = at.toDateString() === now.toDateString();
  if (sameDay) return `сегодня в ${time}`;
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  if (at.toDateString() === yesterday.toDateString()) return `вчера в ${time}`;
  return `${at.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' })} в ${time}`;
}

/**
 * Колокольчик: счётчик непрочитанного (и в заголовке вкладки), по щелчку — список «Сегодня / Ранее». Щелчок по уведомлению ведёт по
 * ссылке и отмечает его прочитанным. Живое обновление — личный хаб; на случай обрыва — опрос раз в минуту.
 */
export function NotificationBell({ position = 'right-end' }: { position?: 'right-end' | 'bottom-end' }) {
  const [opened, setOpened] = useState(false);
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const queryClient = useQueryClient();
  const count = useApi<NotificationCount>([notificationsKey, 'count'], '/api/notifications/count', { refetchInterval: 60_000 });
  const list = useApi<NotificationItem[]>([notificationsKey, 'list'], opened ? '/api/notifications?take=30' : null);
  const unread = count.data?.unread ?? 0;

  useEffect(() => {
    // После смены страницы заголовок ставит сама страница — счётчик дописывается следом.
    const timer = setTimeout(() => {
      const bare = document.title.replace(titlePrefix, '');
      document.title = unread > 0 ? `(${unread > 99 ? '99+' : unread}) ${bare}` : bare;
    }, 0);
    return () => clearTimeout(timer);
  }, [unread, pathname]);

  const refresh = () => queryClient.invalidateQueries({ queryKey: [notificationsKey] });
  const open = async (item: NotificationItem) => {
    setOpened(false);
    if (!item.read) await apiFetch(`/api/notifications/${item.id}/read`, { method: 'POST' });
    void refresh();
    if (item.link) navigate(item.link);
  };
  const readAll = async () => {
    await apiFetch('/api/notifications/read-all', { method: 'POST' });
    void refresh();
  };

  const items = list.data ?? [];
  const today = new Date().toDateString();
  const groups = [
    { title: t.notify.today, items: items.filter((n) => new Date(n.at).toDateString() === today) },
    { title: t.notify.earlier, items: items.filter((n) => new Date(n.at).toDateString() !== today) },
  ].filter((g) => g.items.length > 0);

  return (
    <Popover opened={opened} onChange={setOpened} position={position} width={380} shadow="md" withinPortal>
      <Popover.Target>
        <Tooltip label={t.notify.bell} position="right" disabled={opened}>
          <Indicator label={unread > 99 ? '99+' : unread} size={16} disabled={unread === 0} color="red" offset={4}>
            <ActionIcon variant="subtle" size="lg" aria-label={t.notify.bell} onClick={() => setOpened((o) => !o)}>
              <IconBell size={20} stroke={1.6} />
            </ActionIcon>
          </Indicator>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown p={0}>
        <Group justify="space-between" px="sm" py="xs">
          <Text fw={600}>{t.notify.bell}</Text>
          <Group gap={4}>
            {unread > 0 && <Button size="compact-xs" variant="subtle" onClick={() => void readAll()}>{t.notify.readAll}</Button>}
            <Button size="compact-xs" variant="subtle" color="gray" onClick={() => { setOpened(false); navigate('/profile#notifications'); }}>
              {t.notify.settings}
            </Button>
          </Group>
        </Group>
        <Divider />
        <ScrollArea.Autosize mah={460}>
          {items.length === 0 && !list.isLoading && <Text size="sm" c="dimmed" p="md">{t.notify.empty}</Text>}
          {groups.map((group) => (
            <Box key={group.title}>
              <Text size="xs" c="dimmed" fw={600} px="sm" pt="xs" tt="uppercase">{group.title}</Text>
              <Stack gap={0}>
                {group.items.map((item) => (
                  <UnstyledButton key={item.id} onClick={() => void open(item)} px="sm" py={8}
                    style={{ background: item.read ? undefined : 'var(--mantine-primary-color-light)', borderRadius: 4 }}>
                    <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
                      <Stack gap={2} style={{ minWidth: 0 }}>
                        <Group gap={6} wrap="nowrap">
                          <Text size="sm" fw={item.read ? 500 : 700} truncate>{item.title}</Text>
                          {item.count > 1 && <Badge size="xs" variant="light">{item.count}</Badge>}
                        </Group>
                        <Text size="sm" c="dimmed" lineClamp={2}>{item.text}</Text>
                      </Stack>
                      <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>{ago(item.at)}</Text>
                    </Group>
                  </UnstyledButton>
                ))}
              </Stack>
            </Box>
          ))}
        </ScrollArea.Autosize>
      </Popover.Dropdown>
    </Popover>
  );
}
