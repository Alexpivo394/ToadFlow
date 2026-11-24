import type { ReactNode } from 'react';
import { Anchor, Button, Group, Paper, Stack, Title } from '@mantine/core';
import { IconDownload } from '@tabler/icons-react';
import { Link } from 'react-router';
import { downloadCsv, type CsvValue } from '../../lib/csv';
import { t } from '../../i18n/ru';

/**
 * Искра тренда в строке таблицы: медиана по неделям, линия 1,5 px цвета модуля. Пустые недели — разрыв линии, а не ноль:
 * «операций не было» не значит «ждали ноль секунд». Подсказка — значения по неделям.
 */
export function Trend({ values, format }: { values: (number | null)[]; format: (value: number) => string }) {
  const known = values.filter((v): v is number => v != null);
  if (known.length < 2) return null;
  const width = 72;
  const height = 20;
  const max = Math.max(...known);
  const min = Math.min(...known);
  const x = (i: number) => (values.length === 1 ? width / 2 : (i / (values.length - 1)) * (width - 4) + 2);
  const y = (v: number) => (max === min ? height / 2 : height - 2 - ((v - min) / (max - min)) * (height - 4));
  const segments: string[] = [];
  let current = '';
  values.forEach((v, i) => {
    if (v == null) {
      if (current) segments.push(current);
      current = '';
      return;
    }
    current += `${current ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`;
  });
  if (current) segments.push(current);
  const tip = values.map((v, i) => `${i + 1}: ${v == null ? '—' : format(v)}`).join('\n');
  return (
    <svg width={width} height={height} role="img" aria-label={`${t.usage.trend}: ${tip.replace(/\n/g, ', ')}`}>
      <title>{tip}</title>
      {segments.map((d) => <path key={d} d={d} fill="none" stroke="var(--mantine-color-teal-6)" strokeWidth={1.5} strokeLinecap="round" />)}
    </svg>
  );
}

/** Кнопка выгрузки таблицы экрана в CSV. */
export function CsvButton({ fileName, header, rows }: { fileName: string; header: string[]; rows: () => CsvValue[][] }) {
  return (
    <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} onClick={() => downloadCsv(fileName, header, rows())}>
      {t.usage.csv}
    </Button>
  );
}

/** Блок экрана с заголовком и необязательными действиями справа. */
export function Block({ title, actions, children }: { title?: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <Paper withBorder radius="md" p="md">
      <Stack gap="sm">
        {(title || actions) && (
          <Group justify="space-between" wrap="nowrap">
            {title ? <Title order={5}>{title}</Title> : <span />}
            {actions}
          </Group>
        )}
        {children}
      </Stack>
    </Paper>
  );
}

export const PersonLink = ({ id, name }: { id: string; name: string }) => (
  <Anchor component={Link} to={`/usage/people/${id}`} size="sm" onClick={(e) => e.stopPropagation()}>{name}</Anchor>
);

export const MachineLink = ({ machineKey, name }: { machineKey: string; name: string }) => (
  <Anchor component={Link} to={`/usage/machines/${encodeURIComponent(machineKey)}`} size="sm" onClick={(e) => e.stopPropagation()}>{name}</Anchor>
);
