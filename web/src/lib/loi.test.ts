import { describe, expect, it } from 'vitest';
import { formatShare, isComplete, needsValue, newRule, normalizeProfile, ruleProblems } from './loi';

describe('loi rules', () => {
  it('new rule needs categories and parameters', () => expect(ruleProblems(newRule())).toEqual(['не выбраны параметры для проверки', 'не выбраны категории']));

  it('categories or conditions: either is enough', () => {
    const rule = { ...newRule(), Parameters: ['Марка'], Filter: { ...newRule().Filter, Mode: 'CategoriesOrParameters' as const } };
    expect(ruleProblems(rule)).toEqual(['нет ни категорий, ни условий']);
    expect(ruleProblems({ ...rule, Filter: { ...rule.Filter, Conditions: [{ Parameter: 'Система', Operator: 'IsFilled' }] } })).toEqual([]);
  });

  it('unfinished conditions do not count', () => {
    expect(isComplete({ Parameter: ' ', Operator: 'Equals' })).toBe(false);
    expect(isComplete({ Group: [{ Parameter: '' }] })).toBe(false);
    expect(isComplete({ Group: [{ Parameter: 'Длина', Operator: 'GreaterThan', Value: '100' }] })).toBe(true);
  });

  it('operators without a value', () => {
    expect(needsValue('IsEmpty')).toBe(false);
    expect(needsValue('Contains')).toBe(true);
  });

  it('profile from the plugin gets defaults for omitted fields', () =>
    expect(normalizeProfile({ Name: 'ОВ', Rules: [{ Title: 'Трубы' } as never] }).Rules[0].Filter).toEqual({
      Categories: [], Mode: 'CategoriesAndParameters', Logic: 'And', Conditions: [],
    }));

  it('share', () => {
    expect(formatShare(0.8523)).toBe('85,2 %');
    expect(formatShare(null)).toBe('—');
  });
});
