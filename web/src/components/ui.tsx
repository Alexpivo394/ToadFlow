import type { ReactNode } from 'react';
import { Badge, Button, Group, Paper, Text, Title, type ButtonProps } from '@mantine/core';
import { modals } from '@mantine/modals';
import type { JobStatus, RunState } from '../api/types';
import { t } from '../i18n/ru';

export const jobStatusColor: Record<JobStatus, string> = {
  Queued: 'gray',
  Running: 'blue',
  Succeeded: 'green',
  Warning: 'yellow',
  Failed: 'red',
  Cancelled: 'dark',
};

export function JobStatusBadge({ status }: { status: JobStatus }) {
  return (
    <Badge color={jobStatusColor[status]} variant="light">
      {t.jobStatus[status]}
    </Badge>
  );
}

const runStateColor: Record<RunState, string> = { Expanding: 'grape', Queued: 'gray', Running: 'blue', Completed: 'green', Cancelled: 'dark' };

export function RunStateBadge({ state }: { state: RunState }) {
  return (
    <Badge color={runStateColor[state]} variant="light">
      {t.runState[state]}
    </Badge>
  );
}

export function YearBadge({ year, muted }: { year: number | null | undefined; muted?: boolean }) {
  if (!year) return null;
  return (
    <Badge variant="light" color={muted ? 'gray' : 'cyan'} radius="sm">
      R{year}
    </Badge>
  );
}

/** Карточка с заголовком-подписью, как в макетах. */
export function Panel({ title, actions, children }: { title?: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <Paper withBorder p="md" radius="md">
      {(title || actions) && (
        <Group justify="space-between" mb="sm" wrap="nowrap">
          {title && (
            <Text size="xs" fw={700} c="dimmed" tt="uppercase" style={{ letterSpacing: '0.04em' }}>
              {title}
            </Text>
          )}
          {actions}
        </Group>
      )}
      {children}
    </Paper>
  );
}

export function PageTitle({ children, actions }: { children: ReactNode; actions?: ReactNode }) {
  return (
    <Group justify="space-between" mb="md" wrap="wrap">
      <Title order={2}>{children}</Title>
      {actions}
    </Group>
  );
}

/** Кнопка с подтверждением — для необратимых действий («Убить», «Удалить», «Отменить запуск»). */
export function ConfirmButton({ question, onConfirm, children, ...props }: ButtonProps & { question: string; onConfirm: () => void; children: ReactNode }) {
  return (
    <Button
      {...props}
      onClick={() =>
        modals.openConfirmModal({
          title: t.common.confirm,
          children: <Text size="sm">{question}</Text>,
          labels: { confirm: t.common.yes, cancel: t.common.no },
          confirmProps: { color: props.color ?? 'red' },
          onConfirm,
        })
      }
    >
      {children}
    </Button>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return (
    <Text c="dimmed" size="sm" ta="center" py="md">
      {children}
    </Text>
  );
}

/** Логотип ToadFlow с названием — в меню и на странице входа. */
export function Brand({ size = 28, children }: { size?: number; children: ReactNode }) {
  return (
    <Group gap={8} wrap="nowrap">
      <img src="/logo.png" alt="" width={size} height={size} />
      {children}
    </Group>
  );
}
