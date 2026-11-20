import { Link } from 'react-router';
import { Anchor, Box, Group, Loader, Paper, Stack, Text, Title } from '@mantine/core';
import { IconCircleCheck } from '@tabler/icons-react';
import type { AttentionItem } from '../../api/types';

/**
 * «Требует внимания» — главный блок «Обзора»: не показатели, а что сделать или починить; каждая строка ведёт туда, где это
 * делается. Сбои — выше дел. Строк нет — одна зелёная строка «Всё в порядке».
 */
export function AttentionBlock({ items }: { items: AttentionItem[] | undefined }) {
  const sorted = [...(items ?? [])].sort((a, b) => (a.level === b.level ? 0 : a.level === 'Problem' ? -1 : 1));

  return (
    <Paper withBorder p="md" radius="md" component="section" aria-label="Требует внимания">
      <Title order={4} mb={sorted.length ? 'xs' : 0}>Требует внимания</Title>
      {items === undefined ? (
        <Loader size="sm" />
      ) : sorted.length === 0 ? (
        <Group gap={6} mt="xs">
          <IconCircleCheck size={18} color="var(--mantine-color-green-6)" />
          <Text c="green.7" fw={500}>Всё в порядке</Text>
        </Group>
      ) : (
        <Stack gap={6}>
          {sorted.map((item) => (
            <Group key={item.text + item.link} gap={8} wrap="nowrap">
              <Box w={8} h={8} style={{ flexShrink: 0, borderRadius: '50%', background: `var(--mantine-color-${item.level === 'Problem' ? 'red' : 'blue'}-6)` }} />
              <Anchor component={Link} to={item.link} size="sm" c="inherit">{item.text}</Anchor>
            </Group>
          ))}
        </Stack>
      )}
    </Paper>
  );
}
