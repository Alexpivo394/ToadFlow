import { ActionIcon, Button, Checkbox, Group, Select, Stack, Text } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import type { ClashConfig } from '../../../lib/clashConfig';

type Change = (change: (config: ClashConfig) => ClashConfig) => void;

/** Исключения: пары наборов, которые друг с другом не проверяются; наборы в списке — по группам. */
export function ExclusionsTab({ config, readOnly, onChange }: { config: ClashConfig; readOnly: boolean; onChange: Change }) {
  const data = config.groups
    .map((g) => ({ group: g.name, items: config.sets.filter((s) => s.groupId === g.id).map((s) => ({ value: s.id, label: s.name })) }))
    .filter((g) => g.items.length > 0);
  const update = (index: number, patch: { setA?: string; setB?: string }) =>
    onChange((c) => ({ ...c, exclusions: c.exclusions.map((e, i) => (i === index ? { ...e, ...patch } : e)) }));

  return (
    <Stack gap="sm">
      <Checkbox label="Не проверять соединённые элементы MEP (труба и её фитинг)" disabled={readOnly} checked={config.ignoreConnectedMep}
        onChange={(e) => {
          const checked = e.currentTarget.checked;
          onChange((c) => ({ ...c, ignoreConnectedMep: checked }));
        }} />
      <Text size="sm" c="dimmed">Пары наборов ниже друг с другом не проверяются.</Text>
      {config.exclusions.map((e, i) => (
        <Group key={i} gap="xs">
          <Select size="xs" w={260} searchable disabled={readOnly} aria-label="Набор A" data={data} value={e.setA} onChange={(v) => v && update(i, { setA: v })} />
          <Text size="sm">×</Text>
          <Select size="xs" w={260} searchable disabled={readOnly} aria-label="Набор B" data={data} value={e.setB} onChange={(v) => v && update(i, { setB: v })} />
          {!readOnly && (
            <ActionIcon variant="subtle" color="gray" aria-label="Удалить исключение"
              onClick={() => onChange((c) => ({ ...c, exclusions: c.exclusions.filter((_, j) => j !== i) }))}>
              <IconTrash size={14} />
            </ActionIcon>
          )}
        </Group>
      ))}
      {!readOnly && config.sets.length > 1 && (
        <Button size="xs" variant="default" w="fit-content"
          onClick={() => onChange((c) => ({ ...c, exclusions: [...c.exclusions, { setA: c.sets[0].id, setB: c.sets[1].id }] }))}>
          + Исключение
        </Button>
      )}
    </Stack>
  );
}
