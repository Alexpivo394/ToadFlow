import { describe, expect, it } from 'vitest';
import {
  addCondition, addExclusion, addGroup, cellsForSetPair, copySet, emptyConfig, matrixCell, newGroup, newSet, removeGroup, removeSet, setCell,
  setCellTolerance, hashConfig, type ClashConfig,
} from './clashConfig';

const ar = newGroup('АР', { disciplines: ['АР'], modelKeys: [] });
const ov = newGroup('ОВ', { disciplines: ['ОВ'], modelKeys: [] });

describe('clashConfig', () => {
  it('clashConfig_removeSet_dropsItsExclusions', () => {
    const walls = newSet(ar.id, 'Стены');
    const ducts = newSet(ov.id, 'Воздуховоды');
    const pipes = newSet(ov.id, 'Трубы');
    const config: ClashConfig = {
      ...emptyConfig(),
      groups: [ar, ov],
      sets: [walls, ducts, pipes],
      exclusions: [{ setA: walls.id, setB: ducts.id }, { setA: ducts.id, setB: pipes.id }],
    };

    const result = removeSet(config, walls.id);

    expect(result.sets.map((s) => s.name)).toEqual(['Воздуховоды', 'Трубы']);
    expect(result.exclusions).toEqual([{ setA: ducts.id, setB: pipes.id }]);
  });

  it('clashConfig_matrixCell_symmetric', () => {
    const config = setCell({ ...emptyConfig(), groups: [ar, ov] }, ov.id, ar.id, { enabled: true, toleranceMm: 15 });

    expect(matrixCell(config, ar.id, ov.id)).toEqual({ groupA: ov.id, groupB: ar.id, enabled: true, toleranceMm: 15 });
    expect(setCell(config, ar.id, ov.id, { enabled: false }).matrix).toEqual([{ groupA: ov.id, groupB: ar.id, enabled: false, toleranceMm: 15 }]);
    expect(matrixCell(config, ar.id, ar.id)).toBeUndefined();
  });

  it('clashConfig_copySet_newIdSameGroup', () => {
    const walls = newSet(ar.id, 'Стены');
    const config: ClashConfig = { ...emptyConfig(), groups: [ar], sets: [walls] };

    const result = copySet(config, walls.id);

    expect(result.sets).toHaveLength(2);
    expect(result.sets[1].id).not.toBe(walls.id);
    expect(result.sets[1]).toMatchObject({ groupId: ar.id, name: 'Стены (копия)', anyOf: walls.anyOf });
  });

  it('clashConfig_removeGroup_dropsSetsCellsAndExclusions', () => {
    const ramps = newGroup('Проезды', { disciplines: ['АР'], modelKeys: [] });
    const rampSet = newSet(ramps.id, 'Проезды');
    const ducts = newSet(ov.id, 'Воздуховоды');
    const config: ClashConfig = {
      ...emptyConfig(),
      groups: [ar, ov, ramps],
      sets: [rampSet, ducts],
      matrix: [{ groupA: ramps.id, groupB: ov.id, enabled: true, toleranceMm: 50 }, { groupA: ar.id, groupB: ov.id, enabled: true, toleranceMm: 20 }],
      exclusions: [{ setA: rampSet.id, setB: ducts.id }],
    };

    const result = removeGroup(config, ramps.id);

    expect(result.groups.map((g) => g.name)).toEqual(['АР', 'ОВ']);
    expect(result.sets.map((s) => s.name)).toEqual(['Воздуховоды']);
    expect(result.matrix).toHaveLength(1);
    expect(result.exclusions).toEqual([]);
  });

  it('new set has one group with one condition', () => {
    const set = newSet(ar.id, 'Все');

    expect(set.anyOf).toHaveLength(1);
    expect(set.anyOf[0].all).toEqual([{ property: { kind: 'category' }, operator: 'isNotEmpty', value: null }]);
    expect(addGroup(set).anyOf).toHaveLength(2);
    expect(addCondition(set, 0).anyOf[0].all).toHaveLength(2);
  });
});

describe('clashConfig noise to rules', () => {
  const walls = newSet(ar.id, 'Стены');
  const ducts = newSet(ov.id, 'Воздуховоды');
  const base: ClashConfig = { ...emptyConfig(), groups: [ar, ov], sets: [walls, ducts] };

  it('addExclusion_NoDuplicate_EitherOrder', () => {
    const once = addExclusion(base, walls.id, ducts.id);
    const twice = addExclusion(once, ducts.id, walls.id);

    expect(once.exclusions).toEqual([{ setA: walls.id, setB: ducts.id }]);
    expect(twice).toBe(once);
  });

  it('cellsForSetPair_TwoChecks', () => {
    // Клетка, заданная в обоих порядках (несохранённая правка до проверки), — две проверки одной пары наборов.
    const config: ClashConfig = {
      ...base,
      matrix: [
        { groupA: ar.id, groupB: ov.id, enabled: true, toleranceMm: 20 },
        { groupA: ov.id, groupB: ar.id, enabled: true, toleranceMm: 50 },
        { groupA: ar.id, groupB: ar.id, enabled: true, toleranceMm: 10 },
      ],
    };

    expect(cellsForSetPair(config, ducts.id, walls.id).map((c) => c.toleranceMm)).toEqual([20, 50]);
    expect(cellsForSetPair({ ...config, matrix: [{ ...config.matrix[0], enabled: false }] }, walls.id, ducts.id)).toEqual([]);
  });

  it('setCellTolerance_ChangesOnlyThatCell', () => {
    const config = setCell(setCell(base, ar.id, ov.id, { toleranceMm: 20 }), ar.id, ar.id, { toleranceMm: 10 });
    const cell = matrixCell(config, ov.id, ar.id)!;

    const result = setCellTolerance(config, cell, 35);

    expect(result.matrix.map((c) => c.toleranceMm)).toEqual([35, 10]);
  });
});

describe('hashConfig', () => {
  it('HashConfig_SameContentSameKey_DifferentContentDifferentKey', () => {
    const a = { ...emptyConfig(), groups: [ar, ov] };
    expect(hashConfig(a)).toBe(hashConfig(JSON.parse(JSON.stringify(a))));
    expect(hashConfig(a)).not.toBe(hashConfig({ ...a, groups: [ov, ar] }));
  });
});
