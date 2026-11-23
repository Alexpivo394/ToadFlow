import { Link } from 'react-router';
import { Badge, Group, Paper, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconChevronRight } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { t } from '../../i18n/ru';
import { ResolveButton } from '../overview/ResolveButton';

export interface PersonalItem {
  module: string | null;
  text: string;
  link: string;
  tone: 'Action' | 'Info';
  count: number;
  /** Ошибки модуля можно отметить решёнными (Координатор и выше) — то же «Решено», что в «Требует внимания». */
  resolveKey?: string | null;
}

/**
 * Строки «Для вас»: что ждёт именно этого человека. <c>hideEmpty</c> — на «Обзоре» пустой блок не показывается. Кнопка «Решено» —
 * рядом со строкой, не внутри ссылки.
 */
export function TodayBlock({ hideEmpty = false }: { hideEmpty?: boolean }) {
  const { can } = useAuth();
  const today = useApi<{ items: PersonalItem[] }>(['me-today'], '/api/me/today', { refetchInterval: 60_000 });
  const items = today.data?.items ?? [];
  if (hideEmpty && items.length === 0) return null;
  return (
    <Paper component="section" aria-label={t.today.title} withBorder radius="lg" p="md">
      <Stack gap="xs">
        <Text fw={700} size="lg">{t.today.title}</Text>
        {today.data && items.length === 0 && <Text size="sm" c="dimmed">{t.today.nothing}</Text>}
        {items.map((item) => (
          <Group key={item.link + item.text} gap="xs" wrap="nowrap">
            <UnstyledButton component={Link} to={item.link} style={{ flex: 1 }}>
              <Group justify="space-between" wrap="nowrap" p="xs"
                style={{ borderRadius: 8, background: item.tone === 'Action' ? 'var(--mantine-primary-color-light)' : 'var(--mantine-color-default-hover)' }}>
                <Group gap="sm" wrap="nowrap">
                  <Badge size="lg" circle={item.count < 100} color={item.tone === 'Action' ? 'orange' : 'gray'}>{item.count}</Badge>
                  <Text size="sm" fw={500}>{item.text}</Text>
                </Group>
                <IconChevronRight size={16} />
              </Group>
            </UnstyledButton>
            {item.resolveKey && can('Operator') && <ResolveButton resolveKey={item.resolveKey} title={item.text} variant="row" />}
          </Group>
        ))}
      </Stack>
    </Paper>
  );
}
