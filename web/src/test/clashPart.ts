// Общие данные тестов редактора части: часть «Корпус 1» с моделями АР и ОВ, наборы «Стены» и «Воздуховоды».
const ar = { id: 'g-ar', name: 'АР', source: { disciplines: ['АР'], modelKeys: [] } };
const ov = { id: 'g-ov', name: 'ОВ', source: { disciplines: ['ОВ'], modelKeys: [] } };
const any = { property: { kind: 'category' }, operator: 'isNotEmpty', value: null };

export const walls = { id: 's-walls', groupId: 'g-ar', name: 'Стены', anyOf: [{ all: [any] }] };
export const ducts = { id: 's-ducts', groupId: 'g-ov', name: 'Воздуховоды', anyOf: [{ all: [any] }] };

export const configuration = {
  groups: [ar, ov],
  sets: [walls, ducts],
  matrix: [{ groupA: 'g-ar', groupB: 'g-ov', enabled: true, toleranceMm: 20 }],
  exclusions: [],
  ignoreConnectedMep: true,
};

export const draft = (version = 1, config: object = configuration) => ({
  configuration: config,
  version,
  disciplines: ['АР', 'ОВ'],
  models: [
    { modelKey: 'm1', name: 'POL_K1_AR.rvt', discipline: 'АР' },
    { modelKey: 'm2', name: 'POL_K1_OV.rvt', discipline: 'ОВ' },
  ],
  problems: [],
});

export const partResponses = {
  '/api/clash/parts/p1': {
    id: 'p1', name: 'Корпус 1', object: 'Полковая', part: 'корпус 1', updatedAt: '2026-10-28T10:00:00Z',
    models: [
      { modelId: 'm1', name: 'POL_K1_AR.rvt', modelPath: '', parsedDiscipline: 'АР', disciplineOverride: null, discipline: 'АР', latest: null },
      { modelId: 'm2', name: 'POL_K1_OV.rvt', modelPath: '', parsedDiscipline: 'ОВ', disciplineOverride: null, discipline: 'ОВ', latest: null },
    ],
  },
  '/api/clash/parts/p1/draft': draft(),
  '/api/clash/parts/p1/presence': [],
  '/api/clash/parts/p1/preview': { matched: 14, total: 21430, byAnyOf: [14], sample: [] },
  '/api/clash/parts/p1/values': [{ value: 'OST_Walls', count: 5902 }, { value: 'OST_Doors', count: 310 }, { value: 'OST_GenericModel', count: 1204 }],
  '/api/clash/parts/p1/unassigned': { count: 0, byCategory: [] },
  '/api/clash/parts/p1/draft/run': null,
  '/api/clash/parts/p1/draft/summary': { total: 0, checks: [], legacy: false },
  '/api/clash/catalog': { categories: [], parameters: [{ name: 'Марка', sharedGuid: null, count: 10, isNumeric: false, categories: [] }], families: [], types: [], levels: [], worksets: [] },
  '/api/clash/templates': [],
  '/api/clash/models': [],
};
