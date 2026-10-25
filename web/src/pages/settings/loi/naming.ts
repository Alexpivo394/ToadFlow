import type { NamingConfig } from '../../../api/types';

/** Поля справочника именования: встроенные — со своими подписями, остальные — свои поля пользователя. */
export const builtInFields: Record<string, string> = {
  object: 'Объект',
  discipline: 'Раздел',
  part: 'Часть здания',
  ignore: 'Пропустить',
};

export const fieldLabel = (field: string) => builtInFields[field] ?? field;

/** Все поля настроек: встроенные и свои, что встречаются в записях и шаблонах. */
export function fieldsOf(config: NamingConfig): string[] {
  const custom = [...config.entries.map((e) => e.field), ...config.patterns.map((p) => p.field)].filter((f) => f && !(f in builtInFields));
  return [...Object.keys(builtInFields), ...new Set(custom)];
}

export const emptyNaming = (): NamingConfig => ({ separators: '_', entries: [], patterns: [], nameExpressions: [] });
