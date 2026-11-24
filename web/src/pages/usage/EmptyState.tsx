import { Anchor, Paper, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router';
import { t } from '../../i18n/ru';

/** С какой версии ToadTools присылает данные аналитики. */
export const MinToadTools = '4.6.0';

/** Данных ещё нет: откуда они берутся и куда смотреть. */
export function EmptyState() {
  return (
    <Paper withBorder p="lg" radius="md">
      <Stack gap="xs">
        <Title order={4}>{t.usage.emptyTitle}</Title>
        <Text size="sm">{t.usage.emptyText(MinToadTools)}</Text>
        <Anchor component={Link} to="/usage/coverage" size="sm">{t.usage.emptyLink}</Anchor>
      </Stack>
    </Paper>
  );
}
