import { useState } from 'react';
import { ActionIcon, Button, Checkbox, Group, Select, Stack, Text } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { addExclusion, addGroupExclusion, groupExclusionsOf, type ClashConfig } from '../../../lib/clashConfig';

type Change = (change: (config: ClashConfig) => ClashConfig) => void;
type Pending = { setA: string | null; setB: string | null };
type SetOptions = { group: string; items: { value: string; label: string }[] }[];

/** Значение второй стороны «вся группа» в списке: наборы и группы — в одном списке. */
const groupValue = (id: string) => `group:${id}`;
const groupOfValue = (value: string) => (value.startsWith('group:') ? value.slice('group:'.length) : null);

/** Добавляет исключение по сторонам строки: вторая сторона — набор или вся группа. */
function withExclusion(config: ClashConfig, setA: string, b: string): ClashConfig {
  const group = groupOfValue(b);
  return group ? addGroupExclusion(config, setA, group) : addExclusion(config, setA, b);
}

function Row({ setA, setB, dataA, dataB, readOnly, onA, onB, onRemove }: {
  setA: string | null;
  setB: string | null;
  dataA: SetOptions;
  dataB: SetOptions;
  readOnly: boolean;
  onA: (id: string) => void;
  onB: (id: string) => void;
  onRemove: () => void;
}) {
  return (
    <Group gap="xs">
      <Select size="xs" w={260} searchable disabled={readOnly} aria-label="Набор A" placeholder="Набор" data={dataA} value={setA} onChange={(v) => v && onA(v)} />
      <Text size="sm">×</Text>
      <Select size="xs" w={260} searchable disabled={readOnly} aria-label="Набор B" placeholder="Набор или вся группа" data={dataB} value={setB}
        onChange={(v) => v && onB(v)} />
      {!readOnly && (
        <ActionIcon variant="subtle" color="gray" aria-label="Удалить исключение" onClick={onRemove}>
          <IconTrash size={14} />
        </ActionIcon>
      )}
    </Group>
  );
}

/**
 * Исключения: пары наборов, которые друг с другом не проверяются; наборы в списке — по группам. Второй стороной можно выбрать
 * всю группу — тогда набор не проверяется ни с одним набором группы, и с добавленными позже тоже. Новая строка пустая и живёт
 * только на экране: в черновик она уходит, когда выбраны обе стороны.
 */
export function ExclusionsTab({ config, readOnly, onChange }: { config: ClashConfig; readOnly: boolean; onChange: Change }) {
  const [pending, setPending] = useState<Pending[]>([]);
  const dataA = config.groups
    .map((g) => ({ group: g.name, items: config.sets.filter((s) => s.groupId === g.id).map((s) => ({ value: s.id, label: s.name })) }))
    .filter((g) => g.items.length > 0);
  // У второй стороны группа есть всегда, даже без наборов: исключение подействует на наборы, которые в ней появятся.
  const dataB = config.groups.map((g) => ({
    group: g.name,
    items: [{ value: groupValue(g.id), label: `Вся группа «${g.name}»` },
      ...config.sets.filter((s) => s.groupId === g.id).map((s) => ({ value: s.id, label: s.name }))],
  }));
  const groupRows = groupExclusionsOf(config);

  const withoutPair = (c: ClashConfig, index: number): ClashConfig => ({ ...c, exclusions: c.exclusions.filter((_, i) => i !== index) });
  const withoutGroup = (c: ClashConfig, index: number): ClashConfig => ({ ...c, groupExclusions: groupExclusionsOf(c).filter((_, i) => i !== index) });
  const fill = (index: number, patch: Partial<Pending>) => {
    const row = { ...pending[index], ...patch };
    if (row.setA && row.setB) {
      const { setA, setB } = row;
      onChange((c) => withExclusion(c, setA, setB));
      setPending(pending.filter((_, i) => i !== index));
    } else setPending(pending.map((p, i) => (i === index ? row : p)));
  };

  return (
    <Stack gap="sm">
      <Checkbox label="Не проверять соединённые элементы MEP (трубы, воздуховоды, фитинги, арматура, оборудование)" disabled={readOnly}
        checked={config.ignoreConnectedMep}
        onChange={(e) => {
          const checked = e.currentTarget.checked;
          onChange((c) => ({ ...c, ignoreConnectedMep: checked }));
        }} />
      <Text size="sm" c="dimmed">
        Пары ниже друг с другом не проверяются. Пара элементов снимается, только если исключены все сочетания их наборов.
      </Text>
      {config.exclusions.map((e, i) => (
        <Row key={`pair-${i}`} setA={e.setA} setB={e.setB} dataA={dataA} dataB={dataB} readOnly={readOnly}
          onA={(v) => onChange((c) => withExclusion(withoutPair(c, i), v, e.setB))}
          onB={(v) => onChange((c) => withExclusion(withoutPair(c, i), e.setA, v))}
          onRemove={() => onChange((c) => withoutPair(c, i))} />
      ))}
      {groupRows.map((e, i) => (
        <Row key={`group-${i}`} setA={e.setId} setB={groupValue(e.groupId)} dataA={dataA} dataB={dataB} readOnly={readOnly}
          onA={(v) => onChange((c) => withExclusion(withoutGroup(c, i), v, groupValue(e.groupId)))}
          onB={(v) => onChange((c) => withExclusion(withoutGroup(c, i), e.setId, v))}
          onRemove={() => onChange((c) => withoutGroup(c, i))} />
      ))}
      {pending.map((p, i) => (
        <Row key={`new-${i}`} setA={p.setA} setB={p.setB} dataA={dataA} dataB={dataB} readOnly={readOnly} onA={(v) => fill(i, { setA: v })}
          onB={(v) => fill(i, { setB: v })} onRemove={() => setPending(pending.filter((_, j) => j !== i))} />
      ))}
      {!readOnly && config.sets.length > 0 && (
        <Button size="xs" variant="default" w="fit-content" onClick={() => setPending([...pending, { setA: null, setB: null }])}>
          + Исключение
        </Button>
      )}
    </Stack>
  );
}
