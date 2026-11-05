// Конфигурация проверки коллизий — тот же JSON, что ConfigurationJson в Alexpivo394.Revit.Clash.Rules (camelCase, перечисления строками).

export type PropertyKind = 'category' | 'family' | 'type' | 'level' | 'workset' | 'model' | 'parameter';
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

/** Набор, который не проверяется со всей группой — и с наборами, добавленными в неё позже. */
export interface SetGroupExclusion {
  setId: string;
  groupId: string;
}

export interface ClashConfig {
  groups: SetGroup[];
  sets: SelectionSet[];
  matrix: MatrixCell[];
  exclusions: SetPairExclusion[];
  /** Нет у утверждённых версий, сохранённых до появления исключений с группой. */
  groupExclusions?: SetGroupExclusion[];
  ignoreConnectedMep: boolean;
}

/** Исключения с группой; у старой конфигурации без поля — пустой список. */
export const groupExclusionsOf = (config: ClashConfig): SetGroupExclusion[] => config.groupExclusions ?? [];

export const propertyKinds: { value: PropertyKind; label: string }[] = [
  { value: 'category', label: 'Категория' },
  { value: 'family', label: 'Семейство' },
  { value: 'type', label: 'Тип' },
  { value: 'level', label: 'Уровень' },
  { value: 'workset', label: 'Рабочий набор' },
  { value: 'model', label: 'Модель (файл)' },
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

/** Модель (файл) сравнивается по ключу модели: «содержит» и сравнение чисел для неё смысла не имеют. */
export const modelOperators: ConditionOperator[] = ['equals', 'oneOf', 'notEquals'];

/** У «пусто» и «не пусто» значения нет. */
export const needsValue = (operator: ConditionOperator) => operator !== 'isEmpty' && operator !== 'isNotEmpty';

export const emptyConfig = (): ClashConfig => ({ groups: [], sets: [], matrix: [], exclusions: [], groupExclusions: [], ignoreConnectedMep: true });

export const emptyCondition = (): Condition => ({ property: { kind: 'category' }, operator: 'isNotEmpty', value: null });

/**
 * Новый UUID v4. `crypto.randomUUID` есть только в защищённом контексте (HTTPS или localhost),
 * а сервер часто открывают по http://имя-машины — там UUID собирается из `crypto.getRandomValues`.
 */
export function newId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export const newGroup = (name: string, source: GroupSource): SetGroup => ({ id: newId(), name, source });

export const newSet = (groupId: string, name: string): SelectionSet => ({
  id: newId(),
  groupId,
  name,
  anyOf: [{ all: [emptyCondition()] }],
});

/** Копия набора в той же группе — сразу после оригинала. */
export function copySet(config: ClashConfig, id: string): ClashConfig {
  const index = config.sets.findIndex((s) => s.id === id);
  if (index < 0) return config;
  const copy = { ...structuredClone(config.sets[index]), id: newId(), name: `${config.sets[index].name} (копия)` };
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
  groupExclusions: groupExclusionsOf(config).filter((e) => e.setId !== id),
});

/** Удаляет группу с её наборами, клетками матрицы и исключениями этих наборов и самой группы. */
export function removeGroup(config: ClashConfig, id: string): ClashConfig {
  const gone = new Set(config.sets.filter((s) => s.groupId === id).map((s) => s.id));
  return {
    ...config,
    groups: config.groups.filter((g) => g.id !== id),
    sets: config.sets.filter((s) => s.groupId !== id),
    matrix: config.matrix.filter((c) => c.groupA !== id && c.groupB !== id),
    exclusions: config.exclusions.filter((e) => !gone.has(e.setA) && !gone.has(e.setB)),
    groupExclusions: groupExclusionsOf(config).filter((e) => e.groupId !== id && !gone.has(e.setId)),
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

/** Исключение набора со всей группой; уже исключённая — без изменений, та же конфигурация. */
export function addGroupExclusion(config: ClashConfig, setId: string, groupId: string): ClashConfig {
  const current = groupExclusionsOf(config);
  if (current.some((e) => e.setId === setId && e.groupId === groupId)) return config;
  return { ...config, groupExclusions: [...current, { setId, groupId }] };
}

/** Исключена ли пара наборов — явно или исключением одного из них со всей группой другого; порядок не важен. */
export function isExcludedPair(config: ClashConfig, setA: string, setB: string): boolean {
  if (config.exclusions.some((e) => (e.setA === setA && e.setB === setB) || (e.setA === setB && e.setB === setA))) return true;
  const groupOf = (id: string) => config.sets.find((s) => s.id === id)?.groupId;
  const [groupA, groupB] = [groupOf(setA), groupOf(setB)];
  return groupExclusionsOf(config).some((e) => (e.setId === setA && e.groupId === groupB) || (e.setId === setB && e.groupId === groupA));
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

/**
 * Короткий ключ кеша по содержимому (FNV-1a по JSON): ключи запросов предпросмотра не держат весь черновик,
 * а одинаковые черновики дают один ключ.
 */
export function hashConfig(value: unknown): string {
  const text = JSON.stringify(value) ?? '';
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return `${(hash >>> 0).toString(16)}:${text.length}`;
}
