import { useState } from 'react';
import { ActionIcon, Button, Checkbox, Group, Select, Stack, Text } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { addExclusion, type ClashConfig } from '../../../lib/clashConfig';

type Change = (change: (config: ClashConfig) => ClashConfig) => void;
type Pending = { setA: string | null; setB: string | null };
type SetOptions = { group: string; items: { value: string; label: string }[] }[];

function Row({ setA, setB, data, readOnly, onA, onB, onRemove }: {
  setA: string | null;
  setB: string | null;
  data: SetOptions;
  readOnly: boolean;
  onA: (id: string) => void;
  onB: (id: string) => void;
  onRemove: () => void;
}) {
  return (
    <Group gap="xs">
      <Select size="xs" w={260} searchable disabled={readOnly} aria-label="Набор A" placeholder="Набор" data={data} value={setA} onChange={(v) => v && onA(v)} />
      <Text size="sm">×</Text>
      <Select size="xs" w={260} searchable disabled={readOnly} aria-label="Набор B" placeholder="Набор" data={data} value={setB} onChange={(v) => v && onB(v)} />
      {!readOnly && (
        <ActionIcon variant="subtle" color="gray" aria-label="Удалить исключение" onClick={onRemove}>
          <IconTrash size={14} />
        </ActionIcon>
      )}
    </Group>
  );
}

/**
 * Исключения: пары наборов, которые друг с другом не проверяются; наборы в списке — по группам. Новая строка пустая и живёт
 * только на экране: в черновик она уходит, когда выбраны оба набора.
 */
export function ExclusionsTab({ config, readOnly, onChange }: { config: ClashConfig; readOnly: boolean; onChange: Change }) {
  const [pending, setPending] = useState<Pending[]>([]);
  const data = config.groups
    .map((g) => ({ group: g.name, items: config.sets.filter((s) => s.groupId === g.id).map((s) => ({ value: s.id, label: s.name })) }))
    .filter((g) => g.items.length > 0);
  const update = (index: number, patch: { setA?: string; setB?: string }) =>
    onChange((c) => ({ ...c, exclusions: c.exclusions.map((e, i) => (i === index ? { ...e, ...patch } : e)) }));
  const fill = (index: number, patch: Partial<Pending>) => {
    const row = { ...pending[index], ...patch };
    if (row.setA && row.setB) {
      const { setA, setB } = row;
      onChange((c) => addExclusion(c, setA, setB));
      setPending(pending.filter((_, i) => i !== index));
    } else setPending(pending.map((p, i) => (i === index ? row : p)));
  };

  return (
    <Stack gap="sm">
      <Checkbox label="Не проверять соединённые элементы MEP (труба и её фитинг)" disabled={readOnly} checked={config.ignoreConnectedMep}
        onChange={(e) => {
          const checked = e.currentTarget.checked;
          onChange((c) => ({ ...c, ignoreConnectedMep: checked }));
        }} />
      <Text size="sm" c="dimmed">Пары наборов ниже друг с другом не проверяются.</Text>
      {config.exclusions.map((e, i) => (
        <Row key={i} setA={e.setA} setB={e.setB} data={data} readOnly={readOnly} onA={(v) => update(i, { setA: v })} onB={(v) => update(i, { setB: v })}
          onRemove={() => onChange((c) => ({ ...c, exclusions: c.exclusions.filter((_, j) => j !== i) }))} />
      ))}
      {pending.map((p, i) => (
        <Row key={`new-${i}`} setA={p.setA} setB={p.setB} data={data} readOnly={readOnly} onA={(v) => fill(i, { setA: v })} onB={(v) => fill(i, { setB: v })}
          onRemove={() => setPending(pending.filter((_, j) => j !== i))} />
      ))}
      {!readOnly && config.sets.length > 0 && (
        <Button size="xs" variant="default" w="fit-content" onClick={() => setPending([...pending, { setA: null, setB: null }])}>
          + Исключение
        </Button>
      )}
    </Stack>
  );
}
