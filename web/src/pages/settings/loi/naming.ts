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

const tokensKey = (tokens: string) => tokens.split('+').map((t) => t.replace(/\s/g, '').toUpperCase()).filter(Boolean).sort().join('+');

/**
 * Дополнить справочник стандартным: записи с кусками, которых ещё нет, и шаблоны, которых ещё нет. Свои записи
 * (объекты, поправленные разделы) не трогаются. Возвращает новый справочник и сколько добавлено.
 */
export function mergeDefaults(config: NamingConfig, defaults: NamingConfig): { config: NamingConfig; added: number } {
  const tokens = new Set(config.entries.map((e) => tokensKey(e.tokens)));
  const patterns = new Set(config.patterns.map((p) => p.pattern.trim()));
  const entries = defaults.entries.filter((e) => !tokens.has(tokensKey(e.tokens)));
  const newPatterns = defaults.patterns.filter((p) => !patterns.has(p.pattern.trim()));
  return {
    config: { ...config, entries: [...config.entries, ...entries], patterns: [...config.patterns, ...newPatterns] },
    added: entries.length + newPatterns.length,
  };
}
