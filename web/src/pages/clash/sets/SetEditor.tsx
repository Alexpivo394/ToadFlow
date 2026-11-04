import { useState } from 'react';
import { Alert, ActionIcon, Badge, Button, Drawer, Group, MultiSelect, Paper, Select, Stack, Table, Text, TextInput, Autocomplete } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconCube, IconTrash } from '@tabler/icons-react';
import { useApiPost } from '../../../api/queries';
import type { ModelCatalog, PresenceView, SetPreviewResult, ValueCount } from '../../../api/types';
import { categoryLabel } from '../../../lib/categories';
import {
  addCondition, addGroup, type ClashConfig, type Condition, hashConfig, needsValue, operators, propertyKinds, type PropertyRef, type SelectionSet,
  setCondition,
} from '../../../lib/clashConfig';
import { type SetFocus, SetScene } from './SetScene';

const propertyValue = (p: PropertyRef) => (p.kind === 'parameter' ? `parameter:${p.sharedGuid ?? ''}:${p.parameterName ?? ''}` : p.kind);

function parseProperty(value: string): PropertyRef {
  if (!value.startsWith('parameter:')) return { kind: value as PropertyRef['kind'] };
  const [, guid, ...name] = value.split(':');
  return { kind: 'parameter', sharedGuid: guid || null, parameterName: name.join(':') || null };
}

const show = (property: PropertyRef, value: string) => (property.kind === 'category' ? categoryLabel(value) : value);

/** Ключ значений свойства: значения зависят от источника группы (какие модели), а не от наборов. */
export const valuesKey = (partId: string, config: ClashConfig, groupId: string, property: PropertyRef) =>
  ['clash-values', partId, groupId, hashConfig(config.groups.find((g) => g.id === groupId)?.source ?? null), JSON.stringify(property)];

/** Значение условия: реальные значения источника группы с числом элементов; для «одно из» — несколько. Своё значение тоже можно ввести. */
function ValuePicker({ partId, config, groupId, condition, readOnly, onChange }: {
  partId: string;
  config: ClashConfig;
  groupId: string;
  condition: Condition;
  readOnly: boolean;
  onChange: (condition: Condition) => void;
}) {
  const body = { configuration: config, groupId, property: condition.property };
  const values = useApiPost<ValueCount[]>(valuesKey(partId, config, groupId, condition.property), `/api/clash/parts/${partId}/values`, body);
  const data = (values.data ?? []).map((v) => ({ value: v.value, label: `${show(condition.property, v.value)} · ${v.count.toLocaleString('ru-RU')}` }));

  if (condition.operator === 'oneOf') {
    const chosen = condition.values ?? [];
    const all = [...data, ...chosen.filter((c) => !data.some((d) => d.value === c)).map((c) => ({ value: c, label: show(condition.property, c) }))];
    return (
      <MultiSelect size="xs" style={{ flex: 1 }} searchable disabled={readOnly} aria-label="Значения" placeholder="выберите значения" data={all} value={chosen}
        onChange={(v) => onChange({ ...condition, values: v, value: null })} />
    );
  }

  return (
    <Autocomplete size="xs" style={{ flex: 1 }} disabled={readOnly} aria-label="Значение" placeholder="значение" value={condition.value ?? ''}
      data={data.map((d) => d.value)} onChange={(v) => onChange({ ...condition, value: v })}
      renderOption={({ option }) => <Text size="sm">{data.find((d) => d.value === option.value)?.label ?? option.value}</Text>} />
  );
}

function ConditionRow(props: {
  partId: string;
  config: ClashConfig;
  groupId: string;
  condition: Condition;
  catalog: ModelCatalog | undefined;
  readOnly: boolean;
  first: boolean;
  onChange: (condition: Condition | null) => void;
}) {
  const { condition, catalog, readOnly, onChange } = props;
  const parameters = (catalog?.parameters ?? []).map((p) => ({ value: `parameter:${p.sharedGuid ?? ''}:${p.name}`, label: `Параметр: ${p.name}` }));
  return (
    <Group gap={6} wrap="nowrap">
      <Text size="xs" c="dimmed" w={16}>{props.first ? '' : 'и'}</Text>
      <Select size="xs" w={210} searchable disabled={readOnly} aria-label="Свойство" value={propertyValue(condition.property)}
        data={[...propertyKinds.filter((k) => k.value !== 'parameter').map((k) => ({ value: k.value, label: k.label })), ...parameters]}
        onChange={(v) => v && onChange({ ...condition, property: parseProperty(v), value: null, values: null })} />
      <Select size="xs" w={130} disabled={readOnly} aria-label="Оператор" allowDeselect={false} value={condition.operator} data={operators}
        onChange={(v) => v && onChange({ ...condition, operator: v as Condition['operator'] })} />
      {needsValue(condition.operator) ? <ValuePicker {...props} onChange={onChange} /> : <div style={{ flex: 1 }} />}
      {!readOnly && <ActionIcon variant="subtle" color="gray" aria-label="Удалить условие" onClick={() => onChange(null)}><IconTrash size={14} /></ActionIcon>}
    </Group>
  );
}

/**
 * Редактор набора: источник группы, имя, группы условий «или» (внутри — «и») со счётчиком каждой, образец попавших.
 * Счётчики пересчитываются через 400 мс после последней правки.
 */
export function SetEditor({ partId, config, set, source, catalog, readOnly, editing, onChange }: {
  partId: string;
  config: ClashConfig;
  set: SelectionSet;
  source: string;
  catalog: ModelCatalog | undefined;
  readOnly: boolean;
  /** Кто ещё сейчас открыл этот набор. */
  editing: PresenceView[];
  onChange: (set: SelectionSet) => void;
}) {
  const [debounced] = useDebouncedValue(config, 400);
  // Набор только что добавлен и ещё не попал в отложенный черновик — считать нечего (сервер ответил бы 400).
  const known = debounced.sets.some((s) => s.id === set.id);
  const preview = useApiPost<SetPreviewResult>(['clash-preview', partId, set.id, hashConfig(debounced)], `/api/clash/parts/${partId}/preview`,
    known ? { configuration: debounced, setId: set.id } : null, { gcTime: 30_000 });
  // Набор в 3D: undefined — закрыт, null — весь набор, образец — кадр на нём.
  const [scene3d, setScene3d] = useState<SetFocus | null | undefined>(undefined);

  return (
    <Stack gap="sm" style={{ flex: 1, minWidth: 0 }}>
      {editing.length > 0 && <Alert color="teal" py={6}>{editing.map((p) => p.displayName).join(', ')} сейчас правит этот набор</Alert>}
      <Text size="xs" c="dimmed">Берёт элементы из: {source}</Text>
      <TextInput label="Имя набора" disabled={readOnly} value={set.name} onChange={(e) => onChange({ ...set, name: e.currentTarget.value })} />
      {set.anyOf.map((group, g) => (
        <Stack key={g} gap={4}>
          {g > 0 && <Text size="xs" c="dimmed" ta="center">— или —</Text>}
          <Paper withBorder p="xs" radius="md">
            <Group justify="flex-end" mb={4}>
              <Badge variant="light" color="green">попало {(preview.data?.byAnyOf[g] ?? 0).toLocaleString('ru-RU')}</Badge>
            </Group>
            <Stack gap={4}>
              {group.all.map((condition, i) => (
                <ConditionRow key={i} partId={partId} config={config} groupId={set.groupId} condition={condition} catalog={catalog} readOnly={readOnly}
                  first={i === 0} onChange={(c) => onChange(setCondition(set, g, i, c))} />
              ))}
              {!readOnly && <Button size="compact-xs" variant="subtle" w="fit-content" onClick={() => onChange(addCondition(set, g))}>+ условие</Button>}
            </Stack>
          </Paper>
        </Stack>
      ))}
      {!readOnly && <Button size="xs" variant="default" w="fit-content" onClick={() => onChange(addGroup(set))}>+ группа условий «или»</Button>}
      {preview.data && (
        <Stack gap={4}>
          <Text size="sm">
            В набор попало <b>{preview.data.matched.toLocaleString('ru-RU')}</b> из {preview.data.total.toLocaleString('ru-RU')} элементов источника
          </Text>
          {!readOnly && (
            <Button size="compact-xs" variant="light" w="fit-content" leftSection={<IconCube size={14} />} onClick={() => setScene3d(null)}>
              Показать набор в 3D
            </Button>
          )}
          {preview.data.sample.length > 0 && (
            <Table fz="xs" verticalSpacing={2}>
              <Table.Tbody>
                {preview.data.sample.map((e) => (
                  <Table.Tr key={`${e.modelKey}-${e.uniqueId}`}>
                    <Table.Td>{categoryLabel(e.category)}</Table.Td>
                    <Table.Td>{e.family ?? '—'}</Table.Td>
                    <Table.Td>{e.type ?? '—'}</Table.Td>
                    <Table.Td>ID {e.elementId}</Table.Td>
                    {!readOnly && (
                      <Table.Td w={28}>
                        <ActionIcon size="xs" variant="subtle" aria-label={`3D образца ID ${e.elementId}`}
                          onClick={() => setScene3d({ model: e.modelKey, uniqueId: e.uniqueId })}>
                          <IconCube size={12} />
                        </ActionIcon>
                      </Table.Td>
                    )}
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </Stack>
      )}
      {scene3d !== undefined && (
        <Drawer opened onClose={() => setScene3d(undefined)} position="right" size="xl" title={`Набор «${set.name}» в 3D`}>
          <SetScene key={scene3d ? `${scene3d.model}/${scene3d.uniqueId}` : 'all'} partId={partId} config={config} setId={set.id} focus={scene3d} />
        </Drawer>
      )}
    </Stack>
  );
}
