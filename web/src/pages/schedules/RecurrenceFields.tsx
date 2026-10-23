import { Chip, Group, NumberInput, SegmentedControl, Stack } from '@mantine/core';
import { DateTimePicker, TimeInput } from '@mantine/dates';
import type { RecurrenceKind } from '../../api/types';
import { dayNames } from './recurrenceText';

export interface RecurrenceValue {
  recurrenceKind: RecurrenceKind;
  daysOfWeekMask: number;
  timeOfDay: string;
  intervalHours: number | null;
  onceAt: string | null;
}

export function RecurrenceFields({ value, onChange, readOnly }: { value: RecurrenceValue; onChange: (v: RecurrenceValue) => void; readOnly?: boolean }) {
  const set = (patch: Partial<RecurrenceValue>) => onChange({ ...value, ...patch });
  const selectedDays = dayNames.map((_, i) => String(i)).filter((i) => (value.daysOfWeekMask & (1 << Number(i))) !== 0);

  return (
    <Stack gap="sm">
      <SegmentedControl
        disabled={readOnly}
        value={value.recurrenceKind}
        onChange={(v) => set({ recurrenceKind: v as RecurrenceKind })}
        data={[
          { value: 'Weekly', label: 'По дням недели' },
          { value: 'EveryNHours', label: 'Каждые N часов' },
          { value: 'Once', label: 'Один раз' },
        ]}
      />
      {value.recurrenceKind === 'Weekly' && (
        <Chip.Group multiple value={selectedDays} onChange={(days) => set({ daysOfWeekMask: days.reduce((mask, d) => mask | (1 << Number(d)), 0) })}>
          <Group gap={4}>
            {dayNames.map((name, i) => (
              <Chip key={name} value={String(i)} size="xs" disabled={readOnly}>
                {name}
              </Chip>
            ))}
          </Group>
        </Chip.Group>
      )}
      {value.recurrenceKind === 'EveryNHours' && (
        <NumberInput label="Каждые, часов" min={1} max={24} value={value.intervalHours ?? 4} disabled={readOnly}
          onChange={(v) => set({ intervalHours: typeof v === 'number' ? v : null })} />
      )}
      {value.recurrenceKind !== 'Once' ? (
        <TimeInput
          label={value.recurrenceKind === 'Weekly' ? 'Время' : 'Начиная с'}
          description="по часовому поясу из «Общих»"
          value={value.timeOfDay.slice(0, 5)}
          disabled={readOnly}
          onChange={(e) => set({ timeOfDay: `${e.currentTarget.value || '00:00'}:00` })}
        />
      ) : (
        <DateTimePicker
          label="Когда"
          value={value.onceAt ? new Date(value.onceAt) : null}
          disabled={readOnly}
          onChange={(v) => set({ onceAt: v ? new Date(v).toISOString() : null })}
        />
      )}
    </Stack>
  );
}
