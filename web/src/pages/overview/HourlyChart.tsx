import { Group, Text } from '@mantine/core';
import type { HourPoint } from '../../api/types';

const parts = [
  { key: 'succeeded', label: 'готово', color: 'var(--mantine-color-green-5)' },
  { key: 'warning', label: 'с предупреждением', color: 'var(--mantine-color-orange-5)' },
  { key: 'failed', label: 'ошибка', color: 'var(--mantine-color-red-6)' },
] as const;

const height = 110;
const slot = 30;

/** «Задания за сутки»: 24 столбика по часам, сложенные из готовых, с предупреждением и ошибок. Часы — в поясе браузера. */
export function HourlyChart({ hourly }: { hourly: HourPoint[] }) {
  const max = Math.max(1, ...hourly.map((h) => h.succeeded + h.warning + h.failed));
  const unit = (height - 4) / max;
  const label = (iso: string) => `${String(new Date(iso).getHours()).padStart(2, '0')}:00`;
  const midnight = hourly.findIndex((h, i) => i > 0 && new Date(h.hour).getHours() === 0);

  return (
    <section aria-label="Задания за сутки">
      <Group justify="space-between" mb={6}>
        <Text fw={650}>Задания за сутки</Text>
        <Group gap={14}>
          {parts.map((p) => (
            <Group key={p.key} gap={5}>
              <span style={{ width: 10, height: 10, borderRadius: 3, background: p.color, display: 'inline-block' }} />
              <Text size="xs" c="dimmed">{p.label}</Text>
            </Group>
          ))}
        </Group>
      </Group>
      <svg viewBox={`0 0 ${hourly.length * slot} ${height}`} width="100%" height={height} preserveAspectRatio="none" role="img" aria-label="Задания по часам">
        {hourly.map((h, i) => {
          let y = height;
          return (
            <g key={h.hour} data-testid="hour">
              <title>{`${label(h.hour)}: готово ${h.succeeded}, с предупреждением ${h.warning}, ошибок ${h.failed}`}</title>
              {parts.map((p) => {
                const n = h[p.key];
                if (n === 0) return null;
                y -= n * unit;
                return <rect key={p.key} x={i * slot + 4} y={y} width={slot - 8} height={Math.max(1, n * unit - 1)} rx={3} fill={p.color} />;
              })}
              {h.succeeded + h.warning + h.failed === 0 && <rect x={i * slot + 4} y={height - 2} width={slot - 8} height={2} rx={1} fill="var(--mantine-color-default-border)" />}
            </g>
          );
        })}
      </svg>
      <Group justify="space-between">
        <Text size="xs" c="dimmed">{hourly.length ? label(hourly[0].hour) : ''}</Text>
        {midnight > 0 && <Text size="xs" c="dimmed">00:00</Text>}
        <Text size="xs" c="dimmed">сейчас</Text>
      </Group>
    </section>
  );
}
