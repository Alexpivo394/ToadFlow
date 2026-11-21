import { useId } from 'react';
import { Link } from 'react-router';
import { Group, Paper, Text } from '@mantine/core';
import type { OverviewTile } from '../../api/types';
import { moduleInfo, type ModuleName } from '../../spaces/modules';
import { sparkPath } from './greeting';

const width = 280;
const height = 56;

/** График плитки за две недели: линия цвета модуля и заливка под ней. */
function Sparkline({ module, values, color }: { module: string; values: number[]; color: string }) {
  const id = `spark${useId().replace(/[^a-zA-Z0-9]/g, '')}`; // символы useId в url(#…) ломают ссылку на градиент
  const line = sparkPath(values, width, height);
  return (
    <svg className="ov-spark" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" data-testid={`spark-${module}`} aria-hidden>
      <defs>
        <linearGradient id={id} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor={color} stopOpacity={0.28} />
          <stop offset="1" stopColor={color} stopOpacity={0} />
        </linearGradient>
      </defs>
      <path d={`${line} L${width} ${height} L0 ${height}Z`} fill={`url(#${id})`} />
      <path d={line} fill="none" stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

/**
 * Плитка модуля: значок цвета модуля, главное число с изменением, подпись, до трёх строк пояснений и график за две недели;
 * щелчок — в модуль. Модуль не отдал данные — плитка приглушена, но ведёт туда же.
 */
export function ModuleTile({ tile, title }: { tile: OverviewTile; title: string }) {
  const muted = tile.value === '—';
  const info = moduleInfo[tile.module as ModuleName];
  const color = `var(--mantine-color-${info?.color ?? 'gray'}-6)`;
  const Icon = info?.icon;
  return (
    <Paper withBorder p="md" radius="lg" component={Link} to={tile.link} className="ov-tile" data-muted={muted ? 'true' : 'false'} aria-label={title}>
      <div className="ov-tile-body">
        <Group gap={10} wrap="nowrap">
          <div className="ov-tile-icon" style={{ background: color }}>{Icon && <Icon size={20} />}</div>
          <Text size="xs" fw={700} c="dimmed" tt="uppercase">{title}</Text>
        </Group>
        <Group gap={8} align="baseline" mt={10}>
          <Text fz={34} fw={700} lh={1.1} style={{ letterSpacing: -0.5 }}>{tile.value}</Text>
          {tile.trend && <Text size="sm" fw={600} c={tile.trend.startsWith('▲') ? 'green' : tile.trend.startsWith('▼') ? 'red' : 'dimmed'}>{tile.trend}</Text>}
        </Group>
        <Text size="sm" c="dimmed">{tile.label}</Text>
        {tile.lines.slice(0, 3).map((line) => <Text key={line} size="xs" c="dimmed">{line}</Text>)}
      </div>
      {tile.series && tile.series.length > 1 && <Sparkline module={tile.module} values={tile.series} color={color} />}
    </Paper>
  );
}
