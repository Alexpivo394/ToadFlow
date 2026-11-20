import { describe, expect, it } from 'vitest';
import type { NamingConfig } from '../../../api/types';
import { mergeDefaults } from './naming';

const defaults: NamingConfig = {
  separators: '_',
  entries: [{ field: 'discipline', tokens: 'AR', value: 'АР', customer: null }, { field: 'discipline', tokens: 'OV', value: 'ОВ', customer: null }],
  patterns: [{ field: 'part', pattern: '^K(\\d+)$', value: 'корпус $1' }],
  nameExpressions: [],
};

describe('mergeDefaults', () => {
  it('adds only what is missing and keeps own entries', () => {
    const own: NamingConfig = {
      separators: '_-',
      entries: [{ field: 'object', tokens: 'POL', value: 'Полковая', customer: null }, { field: 'discipline', tokens: ' ar ', value: 'Архитектура', customer: null }],
      patterns: [],
      nameExpressions: [],
    };

    const { config, added } = mergeDefaults(own, defaults);

    expect(added).toBe(2); // OV и шаблон корпуса; AR уже есть — со своим значением
    expect(config.separators).toBe('_-');
    expect(config.entries.map((e) => e.tokens)).toEqual(['POL', ' ar ', 'OV']);
    expect(config.patterns).toHaveLength(1);
    expect(mergeDefaults(config, defaults).added).toBe(0);
  });
});
