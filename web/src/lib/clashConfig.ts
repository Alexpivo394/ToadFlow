// Конфигурация проверки коллизий — тот же JSON, что ConfigurationJson в Alexpivo394.Revit.Clash.Rules (camelCase, перечисления строками).

export type PropertyKind = 'category' | 'family' | 'type' | 'level' | 'workset' | 'parameter';
export type ConditionOperator = 'equals' | 'notEquals' | 'contains' | 'notContains' | 'greater' | 'less' | 'isEmpty' | 'isNotEmpty' | 'oneOf';

export interface PropertyRef {
  kind: PropertyKind;
  parameterName?: string | null;
  sharedGuid?: string | null;
}

export interface Condition {
  property: PropertyRef;
  operator: ConditionOperator;
  value: string | null;
  /** Значения для «одно из». */
  values?: string[] | null;
}

export interface ConditionGroup {
  all: Condition[];
}

/** Откуда группа берёт элементы: модели разделов части и (или) конкретные модели (ключ — Id модели в форме N). */
export interface GroupSource {
  disciplines: string[];
  modelKeys: string[];
}

/** Группа проверки — папка наборов и строка/столбец матрицы. */
export interface SetGroup {
  id: string;
  name: string;
  source: GroupSource;
}

export interface SelectionSet {
  id: string;
  groupId: string;
  name: string;
  anyOf: ConditionGroup[];
}

export interface MatrixCell {
  groupA: string;
  groupB: string;
  enabled: boolean;
  toleranceMm: number;
}

export interface SetPairExclusion {
  setA: string;
  setB: string;
}

export interface ClashConfig {
  groups: SetGroup[];
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
  { value: 'oneOf', label: 'одно из' },
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

export const emptyConfig = (): ClashConfig => ({ groups: [], sets: [], matrix: [], exclusions: [], ignoreConnectedMep: true });

export const emptyCondition = (): Condition => ({ property: { kind: 'category' }, operator: 'isNotEmpty', value: null });

export const newGroup = (name: string, source: GroupSource): SetGroup => ({ id: crypto.randomUUID(), name, source });

export const newSet = (groupId: string, name: string): SelectionSet => ({
  id: crypto.randomUUID(),
  groupId,
  name,
  anyOf: [{ all: [emptyCondition()] }],
});

/** Копия набора в той же группе — сразу после оригинала. */
export function copySet(config: ClashConfig, id: string): ClashConfig {
  const index = config.sets.findIndex((s) => s.id === id);
  if (index < 0) return config;
  const copy = { ...structuredClone(config.sets[index]), id: crypto.randomUUID(), name: `${config.sets[index].name} (копия)` };
  return { ...config, sets: [...config.sets.slice(0, index + 1), copy, ...config.sets.slice(index + 1)] };
}

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

/** Удаляет группу с её наборами, клетками матрицы и исключениями этих наборов. */
export function removeGroup(config: ClashConfig, id: string): ClashConfig {
  const gone = new Set(config.sets.filter((s) => s.groupId === id).map((s) => s.id));
  return {
    ...config,
    groups: config.groups.filter((g) => g.id !== id),
    sets: config.sets.filter((s) => s.groupId !== id),
    matrix: config.matrix.filter((c) => c.groupA !== id && c.groupB !== id),
    exclusions: config.exclusions.filter((e) => !gone.has(e.setA) && !gone.has(e.setB)),
  };
}

export const replaceSet = (config: ClashConfig, set: SelectionSet): ClashConfig => ({ ...config, sets: config.sets.map((s) => (s.id === set.id ? set : s)) });

export const replaceGroup = (config: ClashConfig, group: SetGroup): ClashConfig => ({
  ...config,
  groups: config.groups.map((g) => (g.id === group.id ? group : g)),
});

/** Клетка пары групп без учёта порядка. */
export const matrixCell = (config: ClashConfig, a: string, b: string): MatrixCell | undefined =>
  config.matrix.find((c) => (c.groupA === a && c.groupB === b) || (c.groupA === b && c.groupB === a));

export function setCell(config: ClashConfig, a: string, b: string, patch: Partial<Pick<MatrixCell, 'enabled' | 'toleranceMm'>>): ClashConfig {
  const existing = matrixCell(config, a, b);
  if (existing) return { ...config, matrix: config.matrix.map((c) => (c === existing ? { ...c, ...patch } : c)) };
  return { ...config, matrix: [...config.matrix, { groupA: a, groupB: b, enabled: true, toleranceMm: 20, ...patch }] };
}

/** Исключение пары наборов; уже исключённая пара (в любом порядке) — без изменений, та же конфигурация. */
export function addExclusion(config: ClashConfig, setA: string, setB: string): ClashConfig {
  if (config.exclusions.some((e) => (e.setA === setA && e.setB === setB) || (e.setA === setB && e.setB === setA))) return config;
  return { ...config, exclusions: [...config.exclusions, { setA, setB }] };
}

/** Включённые клетки матрицы, в которые попадает пара наборов: по группам наборов, без учёта порядка. */
export function cellsForSetPair(config: ClashConfig, setA: string, setB: string): MatrixCell[] {
  const groupA = config.sets.find((s) => s.id === setA)?.groupId;
  const groupB = config.sets.find((s) => s.id === setB)?.groupId;
  if (!groupA || !groupB) return [];
  return config.matrix.filter((c) => c.enabled && ((c.groupA === groupA && c.groupB === groupB) || (c.groupA === groupB && c.groupB === groupA)));
}

/** Допуск одной клетки — записи матрицы с теми же группами в том же порядке (другой порядок — другая запись). */
export const setCellTolerance = (config: ClashConfig, cell: MatrixCell, mm: number): ClashConfig => ({
  ...config,
  matrix: config.matrix.map((c) => (c.groupA === cell.groupA && c.groupB === cell.groupB ? { ...c, toleranceMm: mm } : c)),
});
