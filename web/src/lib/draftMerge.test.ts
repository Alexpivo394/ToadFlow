import { describe, expect, it } from 'vitest';
import { emptyConfig, newGroup, newSet, type ClashConfig } from './clashConfig';
import { mergeDraft, takeTheirs } from './draftMerge';

const ar = newGroup('АР', { disciplines: ['АР'], modelKeys: [] });
const walls = newSet(ar.id, 'Стены');
const doors = newSet(ar.id, 'Двери');
const base: ClashConfig = { ...emptyConfig(), groups: [ar], sets: [walls, doors] };
const rename = (config: ClashConfig, id: string, name: string): ClashConfig => ({
  ...config,
  sets: config.sets.map((s) => (s.id === id ? { ...s, name } : s)),
});

describe('draftMerge', () => {
  it('draftMerge_LocalEditsSurviveIncoming', () => {
    const server = rename(base, walls.id, 'Стены наружные');
    const local = rename(base, doors.id, 'Двери и люки');

    const merged = mergeDraft(base, server, local);

    expect(merged.sets.map((s) => s.name)).toEqual(['Стены наружные', 'Двери и люки']);
  });

  it('draftMerge_RemoteDeleteOfUntouchedSet', () => {
    const server: ClashConfig = { ...base, sets: [walls] };
    const local = { ...base, matrix: [{ groupA: ar.id, groupB: ar.id, enabled: true, toleranceMm: 20 }] };

    const merged = mergeDraft(base, server, local);

    expect(merged.sets.map((s) => s.name)).toEqual(['Стены']);
    expect(merged.matrix).toHaveLength(1);
  });

  it('draftMerge_GroupExclusions', () => {
    const server: ClashConfig = { ...base, groupExclusions: [{ setId: walls.id, groupId: ar.id }] };
    const local: ClashConfig = { ...base, groupExclusions: [{ setId: doors.id, groupId: ar.id }] };

    expect(mergeDraft(base, server, local).groupExclusions).toEqual([{ setId: walls.id, groupId: ar.id }, { setId: doors.id, groupId: ar.id }]);
    // Удалённый на сервере набор уносит своё исключение; утверждённая версия без поля читается как пустой список.
    expect(mergeDraft(server, { ...server, sets: [doors] }, server).groupExclusions).toEqual([]);
    expect(mergeDraft(base, { ...base, groupExclusions: undefined }, base).groupExclusions).toEqual([]);
    const key = `groupExclusion:${walls.id.replaceAll('-', '')}|${ar.id.replaceAll('-', '')}`;
    expect(takeTheirs(base, server, [key]).groupExclusions).toEqual([{ setId: walls.id, groupId: ar.id }]);
  });

  it('draftMerge_LocalWinsOnSameItem_TakeTheirsReplaces', () => {
    const server = rename(base, walls.id, 'Стены А');
    const local = rename(base, walls.id, 'Стены Б');

    expect(mergeDraft(base, server, local).sets[0].name).toBe('Стены Б');
    expect(takeTheirs(local, server, [`set:${walls.id.replaceAll('-', '')}`]).sets[0].name).toBe('Стены А');
  });
});
