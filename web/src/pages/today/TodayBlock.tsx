import { Link } from 'react-router';
import { Badge, Group, Paper, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconChevronRight } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import { t } from '../../i18n/ru';

export interface PersonalItem {
  module: string | null;
  text: string;
  link: string;
  tone: 'Action' | 'Info';
  count: number;
}

/** Строки «Для вас»: что ждёт именно этого человека. <c>hideEmpty</c> — на «Обзоре» пустой блок не показывается. */
export function TodayBlock({ hideEmpty = false }: { hideEmpty?: boolean }) {
  const today = useApi<{ items: PersonalItem[] }>(['me-today'], '/api/me/today', { refetchInterval: 60_000 });
  const items = today.data?.items ?? [];
  if (hideEmpty && items.length === 0) return null;
  return (
    <Paper withBorder radius="lg" p="md">
      <Stack gap="xs">
        <Text fw={700} size="lg">{t.today.title}</Text>
        {today.data && items.length === 0 && <Text size="sm" c="dimmed">{t.today.nothing}</Text>}
        {items.map((item) => (
          <UnstyledButton key={item.link + item.text} component={Link} to={item.link}>
            <Group justify="space-between" wrap="nowrap" p="xs"
              style={{ borderRadius: 8, background: item.tone === 'Action' ? 'var(--mantine-primary-color-light)' : 'var(--mantine-color-default-hover)' }}>
              <Group gap="sm" wrap="nowrap">
                <Badge size="lg" circle={item.count < 100} color={item.tone === 'Action' ? 'orange' : 'gray'}>{item.count}</Badge>
                <Text size="sm" fw={500}>{item.text}</Text>
              </Group>
              <IconChevronRight size={16} />
            </Group>
          </UnstyledButton>
        ))}
      </Stack>
    </Paper>
  );
}
