// Конфигурация проверки коллизий — тот же JSON, что ConfigurationJson в Alexpivo394.Revit.Clash.Rules (camelCase, перечисления строками).

export type PropertyKind = 'category' | 'family' | 'type' | 'level' | 'workset' | 'parameter';
export type ConditionOperator = 'equals' | 'notEquals' | 'contains' | 'notContains' | 'greater' | 'less' | 'isEmpty' | 'isNotEmpty';

export interface PropertyRef {
  kind: PropertyKind;
  parameterName?: string | null;
  sharedGuid?: string | null;
}

export interface Condition {
  property: PropertyRef;
  operator: ConditionOperator;
  value: string | null;
}

export interface ConditionGroup {
  all: Condition[];
}

export interface SelectionSet {
  id: string;
  discipline: string;
  name: string;
  anyOf: ConditionGroup[];
}

export interface MatrixCell {
  disciplineA: string;
  disciplineB: string;
  enabled: boolean;
  toleranceMm: number;
}

export interface SetPairExclusion {
  setA: string;
  setB: string;
}

export interface ClashConfig {
  sets: SelectionSet[];
  matrix: MatrixCell[];
  exclusions: SetPairExclusion[];
  ignoreConnectedMep: boolean;
}

export const propertyKinds: { value: PropertyKind; label: string }[] = [
  { value: 'category', label: 'Категория' },
  { value: 'family', label: 'Семейство' },
  { value: 'type', label: 'Тип' },
  { value: 'level', label: 'Уровень' },
  { value: 'workset', label: 'Рабочий набор' },
  { value: 'parameter', label: 'Параметр' },
];

export const operators: { value: ConditionOperator; label: string }[] = [
  { value: 'equals', label: 'равно' },
  { value: 'notEquals', label: 'не равно' },
  { value: 'contains', label: 'содержит' },
  { value: 'notContains', label: 'не содержит' },
  { value: 'greater', label: 'больше' },
  { value: 'less', label: 'меньше' },
  { value: 'isEmpty', label: 'пусто' },
  { value: 'isNotEmpty', label: 'не пусто' },
];

/** У «пусто» и «не пусто» значения нет. */
export const needsValue = (operator: ConditionOperator) => operator !== 'isEmpty' && operator !== 'isNotEmpty';

export const emptyConfig = (): ClashConfig => ({ sets: [], matrix: [], exclusions: [], ignoreConnectedMep: true });

export const emptyCondition = (): Condition => ({ property: { kind: 'category' }, operator: 'isNotEmpty', value: null });

export const newSet = (discipline: string, name: string): SelectionSet => ({
  id: crypto.randomUUID(),
  discipline,
  name,
  anyOf: [{ all: [emptyCondition()] }],
});

export const addGroup = (set: SelectionSet): SelectionSet => ({ ...set, anyOf: [...set.anyOf, { all: [emptyCondition()] }] });

export const addCondition = (set: SelectionSet, group: number): SelectionSet => ({
  ...set,
  anyOf: set.anyOf.map((g, i) => (i === group ? { all: [...g.all, emptyCondition()] } : g)),
});

/** Замена условия; пустая группа удаляется вместе с последним условием. */
export function setCondition(set: SelectionSet, group: number, index: number, condition: Condition | null): SelectionSet {
  const anyOf = set.anyOf
    .map((g, i) => (i === group ? { all: condition ? g.all.map((c, j) => (j === index ? condition : c)) : g.all.filter((_, j) => j !== index) } : g))
    .filter((g) => g.all.length > 0);
  return { ...set, anyOf };
}

/** Удаляет набор и исключения, в которых он участвует. */
export const removeSet = (config: ClashConfig, id: string): ClashConfig => ({
  ...config,
  sets: config.sets.filter((s) => s.id !== id),
  exclusions: config.exclusions.filter((e) => e.setA !== id && e.setB !== id),
});

export const replaceSet = (config: ClashConfig, set: SelectionSet): ClashConfig => ({ ...config, sets: config.sets.map((s) => (s.id === set.id ? set : s)) });

const same = (a: string, b: string) => a.localeCompare(b, 'ru', { sensitivity: 'accent' }) === 0;

/** Клетка пары разделов без учёта порядка и регистра. */
export const matrixCell = (config: ClashConfig, a: string, b: string): MatrixCell | undefined =>
  config.matrix.find((c) => (same(c.disciplineA, a) && same(c.disciplineB, b)) || (same(c.disciplineA, b) && same(c.disciplineB, a)));

export function setCell(config: ClashConfig, a: string, b: string, patch: Partial<Pick<MatrixCell, 'enabled' | 'toleranceMm'>>): ClashConfig {
  const existing = matrixCell(config, a, b);
  if (existing) return { ...config, matrix: config.matrix.map((c) => (c === existing ? { ...c, ...patch } : c)) };
  return { ...config, matrix: [...config.matrix, { disciplineA: a, disciplineB: b, enabled: true, toleranceMm: 20, ...patch }] };
}
