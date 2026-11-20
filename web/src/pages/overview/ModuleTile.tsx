import { Link } from 'react-router';
import { Group, Paper, Text } from '@mantine/core';
import type { OverviewTile } from '../../api/types';

/**
 * Плитка модуля: главное число с подписью, изменение за период, до трёх строк пояснений; щелчок — в модуль. Модуль не отдал
 * данные — плитка приглушена, но ведёт туда же.
 */
export function ModuleTile({ tile, title }: { tile: OverviewTile; title: string }) {
  const muted = tile.value === '—';
  return (
    <Paper withBorder p="md" radius="md" component={Link} to={tile.link} data-muted={muted ? 'true' : 'false'} aria-label={title}
      style={{ textDecoration: 'none', color: 'inherit', opacity: muted ? 0.6 : 1, display: 'block' }}>
      <Text size="xs" fw={700} c="dimmed" tt="uppercase">{title}</Text>
      <Group gap="xs" align="baseline">
        <Text fz={28} fw={700}>{tile.value}</Text>
        {tile.trend && <Text size="sm" c={tile.trend.startsWith('▲') ? 'green' : tile.trend.startsWith('▼') ? 'red' : 'dimmed'}>{tile.trend}</Text>}
      </Group>
      <Text size="sm" c="dimmed">{tile.label}</Text>
      {tile.lines.slice(0, 3).map((line) => <Text key={line} size="xs" c="dimmed">{line}</Text>)}
    </Paper>
  );
}
