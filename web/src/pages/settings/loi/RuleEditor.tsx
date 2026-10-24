import { useMemo } from 'react';
import { ActionIcon, Group, MultiSelect, Paper, Select, SimpleGrid, Stack, TagsInput, Text, TextInput, Tooltip } from '@mantine/core';
import { IconArrowDown, IconArrowUp, IconCopy, IconTrash } from '@tabler/icons-react';
import type { CheckRule, ConditionLogic, SelectionMode } from '../../../api/types';
import { logicLabels, modeLabels, ruleProblems, usesCategories, usesConditions } from '../../../lib/loi';
import { ConditionList } from './ConditionList';
import type { LoiCatalog } from './useLoiCatalog';

const modeOptions = (Object.keys(modeLabels) as SelectionMode[]).map((value) => ({ value, label: modeLabels[value] }));
const logicOptions = (Object.keys(logicLabels) as ConditionLogic[]).map((value) => ({ value, label: logicLabels[value] }));

interface Props {
  rule: CheckRule;
  index: number;
  count: number;
  catalog: LoiCatalog;
  readOnly?: boolean;
  onChange: (rule: CheckRule) => void;
  onMove: (offset: -1 | 1) => void;
  onDuplicate: () => void;
  onDelete: () => void;
}

/** Правило: какие элементы отобрать и какие параметры у них должны быть заполнены. В отчёте — отдельный лист. */
export function RuleEditor({ rule, index, count, catalog, readOnly, onChange, onMove, onDuplicate, onDelete }: Props) {
  const filter = rule.Filter;
  const setFilter = (patch: Partial<CheckRule['Filter']>) => onChange({ ...rule, Filter: { ...filter, ...patch } });

  // Категории правила, которых нет в каталоге (другая версия Revit, каталог не снят), остаются выбранными под своими ключами.
  const categoryData = useMemo(() => {
    const known = new Set(catalog.categories.map((c) => c.value));
    const unknown = filter.Categories.filter((key) => !known.has(key)).map((key) => ({ value: key, label: key }));
    const model = catalog.categories.filter((c) => c.isModel);
    const annotation = catalog.categories.filter((c) => !c.isModel);
    return [
      ...(unknown.length ? [{ group: 'Нет в каталоге', items: unknown }] : []),
      ...(model.length ? [{ group: 'Категории модели', items: model }] : []),
      ...(annotation.length ? [{ group: 'Аннотации', items: annotation }] : []),
    ];
  }, [catalog.categories, filter.Categories]);

  const problems = ruleProblems(rule);
  const suspicious = rule.Parameters.filter(catalog.isSuspicious);

  return (
    <Paper withBorder p="sm" radius="md">
      <Stack gap="sm">
        <Group gap={6} wrap="nowrap" align="flex-end">
          <TextInput label={`Правило ${index + 1}`} placeholder="Название — оно же имя листа в отчёте" value={rule.Title} disabled={readOnly}
            style={{ flex: 1 }} onChange={(e) => onChange({ ...rule, Title: e.currentTarget.value })} />
          {!readOnly && (
            <Group gap={2} wrap="nowrap" mb={4}>
              <Tooltip label="Выше">
                <ActionIcon variant="subtle" disabled={index === 0} onClick={() => onMove(-1)} aria-label="Выше"><IconArrowUp size={16} /></ActionIcon>
              </Tooltip>
              <Tooltip label="Ниже">
                <ActionIcon variant="subtle" disabled={index === count - 1} onClick={() => onMove(1)} aria-label="Ниже"><IconArrowDown size={16} /></ActionIcon>
              </Tooltip>
              <Tooltip label="Дублировать">
                <ActionIcon variant="subtle" onClick={onDuplicate} aria-label="Дублировать"><IconCopy size={16} /></ActionIcon>
              </Tooltip>
              <Tooltip label="Удалить правило">
                <ActionIcon variant="subtle" color="red" onClick={onDelete} aria-label="Удалить правило"><IconTrash size={16} /></ActionIcon>
              </Tooltip>
            </Group>
          )}
        </Group>

        <SimpleGrid cols={{ base: 1, md: usesConditions(filter.Mode) ? 2 : 1 }}>
          <Select label="Отбор элементов" data={modeOptions} value={filter.Mode} allowDeselect={false} disabled={readOnly}
            onChange={(value) => value && setFilter({ Mode: value as SelectionMode })} />
          {usesConditions(filter.Mode) && (
            <Select label="Условия верхнего уровня" data={logicOptions} value={filter.Logic} allowDeselect={false} disabled={readOnly}
              onChange={(value) => value && setFilter({ Logic: value as ConditionLogic })} />
          )}
        </SimpleGrid>

        {usesCategories(filter.Mode) && (
          <MultiSelect
            label="Категории"
            placeholder={catalog.loaded ? 'Начните вводить название категории' : 'Каталог не снят — обновите его в блоке «Каталог Revit»'}
            data={categoryData}
            value={filter.Categories}
            searchable
            clearable
            limit={100}
            disabled={readOnly}
            onChange={(Categories) => setFilter({ Categories })}
          />
        )}

        {usesConditions(filter.Mode) && (
          <div>
            <Text size="sm" fw={500} mb={4}>Условия на параметры</Text>
            <ConditionList conditions={filter.Conditions} parameters={catalog.parameters} readOnly={readOnly}
              onChange={(Conditions) => setFilter({ Conditions })} />
          </div>
        )}

        <TagsInput
          label="Проверяемые параметры"
          description="Можно вставить список из Excel — по строке на параметр"
          placeholder="Имя параметра и Enter"
          data={catalog.parameters}
          limit={50}
          value={rule.Parameters}
          splitChars={[',', ';', '\n', '\t']}
          clearable
          disabled={readOnly}
          onChange={(Parameters) => onChange({ ...rule, Parameters: [...new Set(Parameters.map((p) => p.trim()).filter(Boolean))] })}
        />

        {suspicious.length > 0 && (
          <Text size="xs" c="yellow.7">Нет в каталоге — возможно, опечатка: {suspicious.join(', ')}</Text>
        )}
        {problems.length > 0 && <Text size="xs" c="red">Не готово к запуску: {problems.join('; ')}</Text>}
      </Stack>
    </Paper>
  );
}
