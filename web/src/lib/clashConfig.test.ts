import { describe, expect, it } from 'vitest';
import { addCondition, addGroup, emptyConfig, matrixCell, newSet, removeSet, setCell, type ClashConfig } from './clashConfig';

describe('clashConfig', () => {
  it('clashConfig_removeSet_dropsItsExclusions', () => {
    const walls = newSet('АР', 'Стены');
    const ducts = newSet('ОВ', 'Воздуховоды');
    const pipes = newSet('ВК', 'Трубы');
    const config: ClashConfig = {
      ...emptyConfig(),
      sets: [walls, ducts, pipes],
      exclusions: [{ setA: walls.id, setB: ducts.id }, { setA: ducts.id, setB: pipes.id }],
    };

    const result = removeSet(config, walls.id);

    expect(result.sets.map((s) => s.name)).toEqual(['Воздуховоды', 'Трубы']);
    expect(result.exclusions).toEqual([{ setA: ducts.id, setB: pipes.id }]);
  });

  it('clashConfig_matrixCell_symmetric', () => {
    const config = setCell(emptyConfig(), 'ОВ', 'АР', { enabled: true, toleranceMm: 15 });

    expect(matrixCell(config, 'АР', 'ОВ')).toEqual({ disciplineA: 'ОВ', disciplineB: 'АР', enabled: true, toleranceMm: 15 });
    expect(matrixCell(config, 'ар', 'ов')?.toleranceMm).toBe(15);
    expect(setCell(config, 'АР', 'ОВ', { enabled: false }).matrix).toEqual([{ disciplineA: 'ОВ', disciplineB: 'АР', enabled: false, toleranceMm: 15 }]);
    expect(matrixCell(config, 'АР', 'АР')).toBeUndefined();
  });

  it('new set has one group with one condition', () => {
    const set = newSet('АР', 'Все');

    expect(set.anyOf).toHaveLength(1);
    expect(set.anyOf[0].all).toEqual([{ property: { kind: 'category' }, operator: 'isNotEmpty', value: null }]);
    expect(addGroup(set).anyOf).toHaveLength(2);
    expect(addCondition(set, 0).anyOf[0].all).toHaveLength(2);
  });
});
