import { useState } from 'react';
import { ActionIcon, Autocomplete, Button, Card, Group, Select, Stack, Tabs, Text, TextInput } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { apiFetch } from '../../api/client';
import type { ModelCatalog, SetPreviewResult } from '../../api/types';
import { Empty } from '../../components/ui';
import {
  addCondition, addGroup, needsValue, newSet, operators, propertyKinds, removeSet, replaceSet, setCondition,
  type ClashConfig, type Condition, type PropertyKind, type SelectionSet,
} from '../../lib/clashConfig';

/** Подсказки значения по свойству — из каталога выгрузок моделей части. */
function valueOptions(kind: PropertyKind, catalog: ModelCatalog | undefined): string[] {
  if (!catalog) return [];
  switch (kind) {
    case 'category': return catalog.categories.map((c) => c.key);
    case 'family': return catalog.families;
    case 'type': return catalog.types;
    case 'level': return catalog.levels;
    case 'workset': return catalog.worksets;
    default: return [];
  }
}

function ConditionRow({ condition, catalog, readOnly, onChange }: {
  condition: Condition; catalog: ModelCatalog | undefined; readOnly: boolean; onChange: (c: Condition | null) => void;
}) {
  const { property } = condition;
  const parameters = (catalog?.parameters ?? []).map((p) => p.name);
  return (
    <Group gap="xs" wrap="nowrap" align="flex-end">
      <Select size="xs" w={150} aria-label="Свойство" data={propertyKinds} value={property.kind} allowDeselect={false} disabled={readOnly}
        onChange={(v) => onChange({ ...condition, property: { kind: v as PropertyKind } })} />
      {property.kind === 'parameter' && (
        <Autocomplete size="xs" w={220} aria-label="Параметр" placeholder="имя параметра" data={parameters} limit={30} value={property.parameterName ?? ''}
          disabled={readOnly}
          onChange={(name) => {
            const known = catalog?.parameters.find((p) => p.name === name);
            onChange({ ...condition, property: { kind: 'parameter', parameterName: name, sharedGuid: known?.sharedGuid ?? null } });
          }} />
      )}
      <Select size="xs" w={130} aria-label="Оператор" data={operators} value={condition.operator} allowDeselect={false} disabled={readOnly}
        onChange={(v) => onChange({ ...condition, operator: v as Condition['operator'], value: needsValue(v as Condition['operator']) ? condition.value : null })} />
      {needsValue(condition.operator) && (
        <Autocomplete size="xs" w={240} aria-label="Значение" data={valueOptions(property.kind, catalog)} limit={30} value={condition.value ?? ''}
          disabled={readOnly} onChange={(value) => onChange({ ...condition, value })} />
      )}
      {!readOnly && (
        <ActionIcon variant="subtle" color="red" aria-label="Удалить условие" onClick={() => onChange(null)}>
          <IconTrash size={16} />
        </ActionIcon>
      )}
    </Group>
  );
}

function SetCard({ set, partId, catalog, readOnly, onChange, onRemove }: {
  set: SelectionSet; partId: string; catalog: ModelCatalog | undefined; readOnly: boolean; onChange: (s: SelectionSet) => void; onRemove: () => void;
}) {
  const [preview, setPreview] = useState<SetPreviewResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function count() {
    setError(null);
    try {
      setPreview(await apiFetch<SetPreviewResult>(`/api/clash/parts/${partId}/preview`, {
        method: 'POST', json: { sets: [set], matrix: [], exclusions: [], ignoreConnectedMep: true },
      }));
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <Card withBorder padding="sm">
      <Stack gap="xs">
        <Group justify="space-between">
          <TextInput size="sm" w={320} aria-label="Имя набора" value={set.name} disabled={readOnly} onChange={(e) => onChange({ ...set, name: e.currentTarget.value })} />
          <Group gap="xs">
            <Button size="xs" variant="default" onClick={count}>Сколько элементов попало</Button>
            {!readOnly && <Button size="xs" variant="subtle" color="red" onClick={onRemove}>Удалить набор</Button>}
          </Group>
        </Group>
        {preview && <Text size="sm">Попало {preview.matched.toLocaleString('ru-RU')} из {preview.total.toLocaleString('ru-RU')}</Text>}
        {error && <Text size="sm" c="red">{error}</Text>}
        {set.anyOf.map((group, g) => (
          <Stack key={g} gap={6} pl="sm" style={{ borderLeft: '2px solid var(--mantine-color-default-border)' }}>
            {g > 0 && <Text size="xs" c="dimmed">или</Text>}
            {group.all.map((condition, c) => (
              <ConditionRow key={c} condition={condition} catalog={catalog} readOnly={readOnly}
                onChange={(next) => onChange(setCondition(set, g, c, next))} />
            ))}
            {!readOnly && <Button size="compact-xs" variant="subtle" w="fit-content" onClick={() => onChange(addCondition(set, g))}>+ условие</Button>}
          </Stack>
        ))}
        {!readOnly && <Button size="compact-xs" variant="subtle" w="fit-content" onClick={() => onChange(addGroup(set))}>+ группа «или»</Button>}
      </Stack>
    </Card>
  );
}

/** Наборы по разделам: в группе условия через «и», группы — через «или». */
export function SetEditor({ config, disciplines, partId, catalog, readOnly, onChange }: {
  config: ClashConfig; disciplines: string[]; partId: string; catalog: ModelCatalog | undefined; readOnly: boolean; onChange: (c: ClashConfig) => void;
}) {
  if (disciplines.length === 0) return <Empty>Добавьте в часть модели с разделами</Empty>;
  return (
    <Tabs defaultValue={disciplines[0]} keepMounted={false}>
      <Tabs.List mb="sm">
        {disciplines.map((d) => (
          <Tabs.Tab key={d} value={d}>{d} ({config.sets.filter((s) => s.discipline === d).length})</Tabs.Tab>
        ))}
      </Tabs.List>
      {disciplines.map((d) => (
        <Tabs.Panel key={d} value={d}>
          <Stack gap="sm">
            {config.sets.filter((s) => s.discipline === d).map((set) => (
              <SetCard key={set.id} set={set} partId={partId} catalog={catalog} readOnly={readOnly}
                onChange={(next) => onChange(replaceSet(config, next))} onRemove={() => onChange(removeSet(config, set.id))} />
            ))}
            {!readOnly && (
              <Button variant="default" w="fit-content" onClick={() => onChange({ ...config, sets: [...config.sets, newSet(d, `Набор ${d}`)] })}>
                + Набор
              </Button>
            )}
          </Stack>
        </Tabs.Panel>
      ))}
    </Tabs>
  );
}
