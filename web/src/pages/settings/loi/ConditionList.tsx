import { ActionIcon, Autocomplete, Button, Group, Paper, Select, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import type { Condition, ConditionOperator } from '../../../api/types';
import { needsValue, operatorLabels } from '../../../lib/loi';

const operatorOptions = (Object.keys(operatorLabels) as ConditionOperator[]).map((value) => ({ value, label: operatorLabels[value] }));

interface Props {
  conditions: Condition[];
  parameters: string[];
  readOnly?: boolean;
  /** Группы внутри группы ParamChecker не создаёт — добавлять их можно только на верхнем уровне. */
  allowGroups?: boolean;
  onChange: (conditions: Condition[]) => void;
}

/** Условия отбора: параметр, оператор, значение; группа — условия, связанные через «И». */
export function ConditionList({ conditions, parameters, readOnly, allowGroups = true, onChange }: Props) {
  const update = (index: number, condition: Condition) => onChange(conditions.map((c, i) => (i === index ? condition : c)));
  const remove = (index: number) => onChange(conditions.filter((_, i) => i !== index));

  return (
    <Stack gap={6}>
      {conditions.map((condition, index) =>
        condition.Group ? (
          <Paper key={index} withBorder p="xs" radius="sm">
            <Group justify="space-between" mb={6}>
              <Text size="xs" c="dimmed">Группа: все условия (И)</Text>
              {!readOnly && (
                <Tooltip label="Удалить группу">
                  <ActionIcon variant="subtle" color="red" onClick={() => remove(index)} aria-label="Удалить группу">
                    <IconTrash size={16} />
                  </ActionIcon>
                </Tooltip>
              )}
            </Group>
            <ConditionList conditions={condition.Group} parameters={parameters} readOnly={readOnly} allowGroups={false}
              onChange={(group) => update(index, { Group: group })} />
          </Paper>
        ) : (
          <Group key={index} gap={6} wrap="nowrap" align="flex-start">
            <Autocomplete
              placeholder="Параметр"
              data={parameters}
              limit={50}
              value={condition.Parameter ?? ''}
              disabled={readOnly}
              style={{ flex: 2 }}
              onChange={(Parameter) => update(index, { ...condition, Parameter })}
            />
            <Select
              placeholder="Оператор"
              data={operatorOptions}
              value={condition.Operator ?? null}
              disabled={readOnly}
              allowDeselect={false}
              style={{ flex: 1.3 }}
              onChange={(value) => update(index, { ...condition, Operator: (value as ConditionOperator) ?? undefined })}
            />
            {needsValue(condition.Operator) ? (
              <TextInput placeholder="Значение" value={condition.Value ?? ''} disabled={readOnly} style={{ flex: 1.3 }}
                onChange={(e) => update(index, { ...condition, Value: e.currentTarget.value })} />
            ) : (
              <div style={{ flex: 1.3 }} />
            )}
            {!readOnly && (
              <Tooltip label="Удалить условие">
                <ActionIcon variant="subtle" color="red" mt={4} onClick={() => remove(index)} aria-label="Удалить условие">
                  <IconTrash size={16} />
                </ActionIcon>
              </Tooltip>
            )}
          </Group>
        ),
      )}
      {!readOnly && (
        <Group gap={6}>
          <Button size="compact-xs" variant="default" onClick={() => onChange([...conditions, { Parameter: '', Operator: 'IsFilled' }])}>
            + Условие
          </Button>
          {allowGroups && (
            <Button size="compact-xs" variant="default" onClick={() => onChange([...conditions, { Group: [{ Parameter: '', Operator: 'Equals', Value: '' }] }])}>
              + Группа (И)
            </Button>
          )}
        </Group>
      )}
    </Stack>
  );
}
