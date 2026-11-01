/** Ответы сервера для тестов отчёта прогона. */

export const collision = (n: number, extra: object = {}) => ({
  id: `c${n}`, number: n, status: 'Returned', assignee: 'Both', foundNow: true, depthMm: 40 + n, x: 0, y: 0, z: 0,
  modelA: 'm1', modelNameA: 'POL_K1_AR.rvt', uniqueIdA: `ua${n}`, elementIdA: 1000 + n, disciplineA: 'АР', categoryA: 'OST_Walls', familyA: 'Стена', typeA: '200', levelA: 'Этаж 1',
  modelB: 'm2', modelNameB: 'POL_K1_OV.rvt', uniqueIdB: `ub${n}`, elementIdB: 2000 + n, disciplineB: 'ОВ', categoryB: 'OST_DuctCurves', familyB: 'Воздуховод', typeB: '500x300', levelB: 'Этаж 1',
  checks: ['k-arov'], firstVersion: 1, lastFoundVersion: 2, suggestedPredecessorId: null, suggestedPredecessorNumber: null, imageVersion: 'v1', ...extra,
});

const summary = { found: 30, new: 12, active: 15, returned: 3, fixed: 4, goneByRules: 1, unchecked: 2, fake: 5, reusedPairs: 0 };

const pair = (key: string, label: string, a: string, b: string, open: number, extra: object = {}) =>
  ({ pair: key, label, disciplineA: a, disciplineB: b, open, new: open, returned: 0, fixed: 0, fake: 0, ...extra });

/** Отчёт с разделами АР, ОВ, ВК и парами АР × ОВ, АР × ВК, ОВ × ВК (ключ «ВК × ОВ»). */
export const report = (extra: object = {}) => ({
  run: { runId: 'r2', partId: 'p1', version: 2, finishedAt: '2026-10-31T09:05:00Z', trigger: 'manual', startedBy: 'Иванов', searchSeconds: 12.5 },
  check: { partId: 'p1', name: 'Корпус 1', object: 'Полковая', part: 'корпус 1', version: 2, lastRunAt: null, lastRun: null, open: 30, models: [] },
  summary,
  models: [
    { name: 'POL_K1_AR.rvt', discipline: 'АР', uploadedAt: '2026-10-31T08:00:00Z', unchecked: true },
    { name: 'POL_K1_OV.rvt', discipline: 'ОВ', uploadedAt: '2026-10-31T08:10:00Z', unchecked: false },
  ],
  pairs: [
    pair('АР × ОВ', 'АР × ОВ', 'АР', 'ОВ', 30, { returned: 3, fixed: 4, fake: 5, new: 12 }),
    pair('АР × ВК', 'АР × ВК', 'АР', 'ВК', 2),
    pair('ВК × ОВ', 'ОВ × ВК', 'ОВ', 'ВК', 5),
  ],
  disciplines: [
    { name: 'АР', open: 32, new: 14, returned: 3, fixed: 4, pairs: ['АР × ОВ', 'АР × ВК'] },
    { name: 'ОВ', open: 35, new: 17, returned: 3, fixed: 4, pairs: ['АР × ОВ', 'ВК × ОВ'] },
    { name: 'ВК', open: 7, new: 7, returned: 0, fixed: 0, pairs: ['АР × ВК', 'ВК × ОВ'] },
  ],
  newerRunId: null,
  newerRunAt: null,
  ...extra,
});

export const row = (n: number, level = 'Этаж 1', extra: object = {}) =>
  ({ collision: collision(n, extra), change: 'Returned', lastNote: null, lastNoteBy: null, lastNoteAt: null, level });

export const responses = (extra: Record<string, unknown> = {}) => ({
  '/api/clash/reports/r2': report(),
  '/api/clash/reports/r2/collisions': {
    total: 2,
    items: [
      { ...row(1), lastNote: 'Переносим трубу', lastNoteBy: 'Петров', lastNoteAt: '2026-10-31T10:00:00Z' },
      { ...row(2, 'Этаж 1', { status: 'New' }), change: 'New' },
    ],
  },
  '/api/clash/reports/r2/levels': [{ pair: 'АР × ОВ', level: 'Этаж 1', count: 2 }],
  '/api/clash/reports/r2/chat': { text: 'Коллизии: Полковая · Корпус 1 (v2)' },
  '/api/clash/report-archives/current': null,
  '/api/clash/collisions/c1': { collision: collision(1), feed: [{ at: '2026-10-31T10:00:00Z', kind: 'Note', userName: 'Петров', text: 'Переносим трубу', fromStatus: null, toStatus: null, version: null, depthMm: null }] },
  '/api/clash/collisions/c2': { collision: collision(2), feed: [] },
  ...extra,
});

export const designer = { '/api/auth/me': { id: 'u2', login: 'ov', displayName: 'Проектировщик', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: ['ОВ'] } };

export const pairUrl = (key: string) => '/clash/reports/r2?pair=' + encodeURIComponent(key);
