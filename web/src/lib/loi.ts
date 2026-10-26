import type { CheckProfile, CheckRule, Condition, ConditionLogic, ConditionOperator, ExportJobResult, JobResult, LoiCatalogResult, LoiJobResult, LoiProfileSummary, SelectionMode } from '../api/types';

// Повторяет правила Alexpivo394.Revit.ParamCheck.Profiles (ConditionRules, RunValidator) — для подсказок прямо в редакторе.
// Окончательно профиль проверяет сервер.

export const modeLabels: Record<SelectionMode, string> = {
  CategoriesOnly: 'Только категории',
  ParametersOnly: 'Только условия',
  CategoriesOrParameters: 'Категории ИЛИ условия',
  CategoriesAndParameters: 'Категории И условия',
};

export const logicLabels: Record<ConditionLogic, string> = { And: 'Все условия (И)', Or: 'Любое условие (ИЛИ)' };

/** Порядок — как в ParamChecker. */
export const operatorLabels: Record<ConditionOperator, string> = {
  Equals: 'Равно',
  NotEquals: 'Не равно',
  GreaterThan: 'Больше',
  GreaterThanOrEquals: 'Больше или равно',
  LessThan: 'Меньше',
  LessThanOrEquals: 'Меньше или равно',
  Contains: 'Содержит',
  NotContains: 'Не содержит',
  StartsWith: 'Начинается с',
  EndsWith: 'Заканчивается на',
  Exists: 'Параметр есть',
  NotExists: 'Параметра нет',
  IsFilled: 'Заполнен',
  IsEmpty: 'Не заполнен',
};

export const usesCategories = (mode: SelectionMode) => mode !== 'ParametersOnly';
export const usesConditions = (mode: SelectionMode) => mode !== 'CategoriesOnly';

/** Оператору не нужно значение — поле значения прячется. */
export const needsValue = (op: ConditionOperator | undefined) => !(op === 'Exists' || op === 'NotExists' || op === 'IsFilled' || op === 'IsEmpty');

/** Недописанное условие (без параметра или пустая группа) не учитывается. */
export function isComplete(condition: Condition): boolean {
  return condition.Group ? condition.Group.some(isComplete) : Boolean(condition.Parameter?.trim()) && condition.Operator !== undefined;
}

export const hasConditions = (rule: CheckRule) => rule.Filter.Conditions.some(isComplete);

/** Чего не хватает правилу для запуска; пусто — правило готово. */
export function ruleProblems(rule: CheckRule): string[] {
  const problems: string[] = [];
  const { Mode, Categories } = rule.Filter;
  if (!rule.Parameters.some((p) => p.trim())) problems.push('не выбраны параметры для проверки');
  if ((Mode === 'CategoriesOnly' || Mode === 'CategoriesAndParameters') && Categories.length === 0) problems.push('не выбраны категории');
  if (Mode === 'ParametersOnly' && !hasConditions(rule)) problems.push('отбор только по условиям, но условий нет');
  if (Mode === 'CategoriesOrParameters' && Categories.length === 0 && !hasConditions(rule)) problems.push('нет ни категорий, ни условий');
  return problems;
}

export const newRule = (): CheckRule => ({
  Title: '',
  Filter: { Categories: [], Mode: 'CategoriesAndParameters', Logic: 'And', Conditions: [] },
  Parameters: [],
});

export const newProfile = (): CheckProfile => ({ Name: '', ViewName: '', WorksetKeyword: '', Rules: [newRule()] });

/** Профиль с сервера или из файла: пропущенные поля — значениями по умолчанию, как их понимает ParamChecker. */
export function normalizeProfile(profile: Partial<CheckProfile>): CheckProfile {
  return {
    ...profile,
    Name: profile.Name ?? '',
    ViewName: profile.ViewName ?? '',
    WorksetKeyword: profile.WorksetKeyword ?? '',
    Rules: (profile.Rules ?? []).map((rule) => ({
      Title: rule.Title ?? '',
      Parameters: rule.Parameters ?? [],
      Filter: {
        Categories: rule.Filter?.Categories ?? [],
        Mode: rule.Filter?.Mode ?? 'CategoriesAndParameters',
        Logic: rule.Filter?.Logic ?? 'And',
        Conditions: rule.Filter?.Conditions ?? [],
      },
    })),
  };
}

/** Имя профиля с группой: «Полковая · ОВ». */
export const profileDisplayName = (p: Pick<LoiProfileSummary, 'name' | 'group'>) => (p.group ? `${p.group} · ${p.name}` : p.name);

/** Подпись группы профилей. */
export const groupLabel = (group: string) => group || 'Без группы';

/** Профили по группам: группы по алфавиту, «Без группы» — последней. */
export function groupProfiles<T extends Pick<LoiProfileSummary, 'group' | 'name'>>(profiles: T[]): { group: string; profiles: T[] }[] {
  const groups = new Map<string, T[]>();
  for (const p of profiles) groups.set(p.group, [...(groups.get(p.group) ?? []), p]);
  return [...groups.entries()]
    .sort(([a], [b]) => (a === '' ? 1 : b === '' ? -1 : a.localeCompare(b, 'ru')))
    .map(([group, list]) => ({ group, profiles: list.sort((a, b) => a.name.localeCompare(b.name, 'ru')) }));
}

/** Варианты выбора профиля по группам; недописанные помечены. Подпись — с группой, чтобы выбранный «ОВ» не путался с «ОВ» другого проекта. */
export const profileOptions = (profiles: LoiProfileSummary[]) =>
  groupProfiles(profiles).map(({ group, profiles: list }) => ({
    group: groupLabel(group),
    items: list.map((p) => ({ value: p.id, label: p.problems.length ? `${profileDisplayName(p)} (не готов)` : profileDisplayName(p) })),
  }));

/** Доля — «85,2 %»; null — «—» (нечего было проверять). */
export function formatShare(share: number | null | undefined): string {
  if (share == null) return '—';
  return `${(share * 100).toFixed(1).replace('.', ',')} %`;
}

export const isLoiResult = (result: JobResult | null | undefined): result is LoiJobResult => result != null && 'fillRate' in result;
export const isCatalogResult = (result: JobResult | null | undefined): result is LoiCatalogResult => result != null && 'categories' in result;
export const isExportResult = (result: JobResult | null | undefined): result is ExportJobResult => result != null && 'rvt' in result;
