import type { ClashConfig, MatrixCell, SelectionSet, SetGroup, SetPairExclusion } from './clashConfig';

// Ключи элементов черновика — те же, что у ConfigurationMerge на сервере: Id в форме N (без дефисов), пары — меньший первым.
const n = (id: string) => id.replaceAll('-', '').toLowerCase();
const pair = (kind: string, a: string, b: string) => {
  const [x, y] = [n(a), n(b)];
  return x <= y ? `${kind}:${x}|${y}` : `${kind}:${y}|${x}`;
};

export const flagKey = 'flag:ignoreConnectedMep';
export const groupKey = (g: SetGroup) => `group:${n(g.id)}`;
export const setKey = (s: SelectionSet) => `set:${n(s.id)}`;
export const cellKey = (c: MatrixCell) => pair('cell', c.groupA, c.groupB);
export const exclusionKey = (e: SetPairExclusion) => pair('exclusion', e.setA, e.setB);

const same = (x: unknown, y: unknown) => JSON.stringify(x) === JSON.stringify(y);

/** Канонический вид клетки: группы в порядке ключа, иначе «ОВ × АР» и «АР × ОВ» казались бы разными. */
const ordered = (cells: MatrixCell[]) => cells.map((c) => (n(c.groupA) <= n(c.groupB) ? c : { ...c, groupA: c.groupB, groupB: c.groupA }));

function items<T>(base: T[], server: T[], local: T[], key: (item: T) => string, pick: (b?: T, s?: T, l?: T) => T | undefined): T[] {
  const byKey = (list: T[]) => new Map(list.map((item) => [key(item), item]));
  const [b, s, l] = [byKey(base), byKey(server), byKey(local)];
  const order = [...new Set([...server.map(key), ...local.map(key)])];
  return order.map((k) => pick(b.get(k), s.get(k), l.get(k))).filter((item): item is T => item !== undefined);
}

/**
 * Чужое сохранение поверх моих несохранённых правок: всё, что я не трогал (local совпадает с base), берётся с сервера;
 * то, что я поменял, остаётся моим. Конфликты решает сервер при сохранении — здесь местное всегда сверху.
 */
export function mergeDraft(base: ClashConfig, server: ClashConfig, local: ClashConfig): ClashConfig {
  const pick = <T,>(b?: T, s?: T, l?: T) => (same(b, l) ? s : l);
  const sets = items(base.sets, server.sets, local.sets, setKey, pick);
  const ids = new Set(sets.map((s) => s.id));
  return {
    groups: items(base.groups, server.groups, local.groups, groupKey, pick),
    sets,
    matrix: items(ordered(base.matrix), ordered(server.matrix), ordered(local.matrix), cellKey, pick),
    exclusions: items(base.exclusions, server.exclusions, local.exclusions, exclusionKey, pick).filter((e) => ids.has(e.setA) && ids.has(e.setB)),
    ignoreConnectedMep: base.ignoreConnectedMep === local.ignoreConnectedMep ? server.ignoreConnectedMep : local.ignoreConnectedMep,
  };
}

/** Для названных ключей — вариант сервера («взять чужое»): элемент заменяется, а если на сервере его нет — удаляется. */
export function takeTheirs(local: ClashConfig, server: ClashConfig, keys: string[]): ClashConfig {
  const wanted = new Set(keys);
  const replace = <T,>(mine: T[], theirs: T[], key: (item: T) => string): T[] => {
    const fromServer = new Map(theirs.map((item) => [key(item), item]));
    const kept = mine.filter((item) => !wanted.has(key(item)) || fromServer.has(key(item))).map((item) => (wanted.has(key(item)) ? fromServer.get(key(item))! : item));
    const added = theirs.filter((item) => wanted.has(key(item)) && !mine.some((m) => key(m) === key(item)));
    return [...kept, ...added];
  };
  return {
    groups: replace(local.groups, server.groups, groupKey),
    sets: replace(local.sets, server.sets, setKey),
    matrix: replace(ordered(local.matrix), ordered(server.matrix), cellKey),
    exclusions: replace(local.exclusions, server.exclusions, exclusionKey),
    ignoreConnectedMep: wanted.has(flagKey) ? server.ignoreConnectedMep : local.ignoreConnectedMep,
  };
}
