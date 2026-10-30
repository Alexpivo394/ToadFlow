// Повторяет DTO сервера (camelCase, перечисления строками). Меняется вместе с сервером.

export type UserRole = 'Viewer' | 'Operator' | 'Admin' | 'Designer';
export type AgentState = 'Online' | 'Offline' | 'Draining' | 'Disabled';
export type InstanceState = 'Starting' | 'Idle' | 'Busy' | 'Unresponsive' | 'Restarting';
export type JobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Warning' | 'Failed' | 'Cancelled';
export type RunState = 'Expanding' | 'Queued' | 'Running' | 'Completed' | 'Cancelled';
export type SourceKind = 'RevitServer' | 'Folder';
export type RecurrenceKind = 'Weekly' | 'EveryNHours' | 'Once';
export type StepStatus = 'NotRequested' | 'Done' | 'Skipped' | 'Failed';
export type NwcParameters = 'None' | 'Elements' | 'All';

export interface UserView {
  id: string;
  login: string;
  displayName: string;
  role: UserRole;
  isDisabled: boolean;
  lastLoginAt: string | null;
  /** Заявка на регистрацию ждёт подтверждения. */
  isPending?: boolean;
  position?: string | null;
  email?: string | null;
  phone?: string | null;
  /** Разделы, которые человек ведёт; по ним — «Мои коллизии». */
  disciplines?: string[];
  createdAt?: string;
}

export interface LoginResponse {
  token: string;
  user: UserView;
}

export interface InstalledRevit {
  year: number;
  hasNwcExporter: boolean;
}

export interface VersionLimit {
  year: number;
  maxInstances: number;
}

export interface InstanceStatus {
  slot: number;
  year: number;
  pid: number | null;
  state: InstanceState;
  jobId: string | null;
  stage: string | null;
  stageSince: string | null;
  modelName: string | null;
}

export interface AgentView {
  id: string;
  machineName: string;
  ip: string | null;
  agentVersion: string;
  totalRamMb: number;
  state: AgentState;
  isDraining: boolean;
  lastSeenAt: string | null;
  cpuPercent: number;
  ramUsedMb: number;
  revits: InstalledRevit[];
  limits: VersionLimit[];
  instances: InstanceStatus[];
  isOutdated: boolean;
}

export interface Source {
  id: string;
  name: string;
  kind: SourceKind;
  host: string;
  revitYear: number | null;
  rootPath: string;
}

export interface SourceEntry {
  name: string;
  folderPath: string;
  modelPath: string;
  isFolder: boolean;
}

export interface ModelSelection {
  name: string;
  modelPath: string;
  rvt: boolean;
  nwc: boolean;
}

export interface FolderSelection {
  folderPath: string;
  rvt: boolean;
  nwc: boolean;
}

export interface Selection {
  models: ModelSelection[];
  folders: FolderSelection[];
}

export interface ExportSettings {
  skipLinks: boolean;
  audit: boolean;
  worksetKeyword: string;
  deleteRevitLinks: boolean;
  deleteCad: boolean;
  deletePointClouds: boolean;
  purge: boolean;
  compact: boolean;
  nwcViewName: string;
  nwcSharedCoordinates: boolean;
  nwcConvertElementProperties: boolean;
  nwcParameters: NwcParameters;
  nwcDivideIntoLevels: boolean;
  nwcExportLinks: boolean;
  nwcConvertLinkedCad: boolean;
  nwcExportParts: boolean;
  nwcRoomGeometry: boolean;
  nwcRoomAsAttribute: boolean;
  nwcConvertLights: boolean;
  nwcExportElementIds: boolean;
  nwcExportUrls: boolean;
  nwcFindMissingMaterials: boolean;
  nwcFacetingFactor: number;
}

export type ExportOverrides = Partial<ExportSettings>;

export interface Preset {
  id: string;
  name: string;
  isDefault: boolean;
  settings: ExportSettings;
}

export interface ExportScheduleSettings {
  presetId: string;
  overrides: ExportOverrides;
}

export interface LastRun {
  id: string;
  state: RunState;
  createdAt: string;
  succeeded: number;
  failed: number;
  total: number;
}

export interface ScheduleView {
  id: string;
  name: string;
  moduleKind: string;
  sourceId: string;
  sourceName: string;
  sourceRevitYear: number | null;
  outputFolderTemplate: string;
  selection: Selection;
  moduleSettings: ModuleSettings;
  recurrenceKind: RecurrenceKind;
  daysOfWeekMask: number;
  timeOfDay: string;
  intervalHours: number | null;
  onceAt: string | null;
  priority: number;
  modelTimeoutMinutes: number;
  maxRetries: number;
  isEnabled: boolean;
  nextRunAt: string | null;
  modelCount: number;
  lastRun: LastRun | null;
  createdBy: string;
  createdAt: string;
  updatedBy: string;
  updatedAt: string;
  warnings: string[];
}

export interface ScheduleRequest {
  name: string;
  moduleKind: string;
  sourceId: string;
  outputFolderTemplate: string;
  selection: Selection;
  moduleSettings: ModuleSettings;
  recurrenceKind: RecurrenceKind;
  daysOfWeekMask: number;
  timeOfDay: string;
  intervalHours: number | null;
  onceAt: string | null;
  priority: number;
  modelTimeoutMinutes: number;
  maxRetries: number;
  isEnabled: boolean;
}

export interface ScheduleChange {
  at: string;
  userName: string;
  summary: string;
}

export interface RunCounts {
  succeeded: number;
  warning: number;
  failed: number;
  queued: number;
  running: number;
  cancelled: number;
  total: number;
}

export interface RunRow {
  id: string;
  scheduleId: string | null;
  name: string;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  state: RunState;
  triggeredBy: string | null;
  counts: RunCounts;
  durationSeconds: number | null;
  warning: string | null;
}

export interface ExportJobResult {
  rvt: StepStatus;
  nwc: StepStatus;
  rvtSizeBytes: number | null;
  nwcSizeBytes: number | null;
  rvtPath: string | null;
  nwcPath: string | null;
}

export interface JobRow {
  id: string;
  runId: string;
  runName: string;
  moduleKind: string;
  modelName: string;
  modelPath: string;
  status: JobStatus;
  revitYear: number | null;
  requiresNwc: boolean;
  agentId: string | null;
  agentName: string | null;
  slot: number | null;
  attempt: number;
  maxAttempts: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  durationSeconds: number | null;
  note: string | null;
  result: JobResult | null;
  waitingForRevit: number | null;
  cancelRequested: boolean;
}

export interface Page<T> {
  items: T[];
  total: number;
}

export interface RunDetails {
  run: RunRow;
  jobs: JobRow[];
}

export interface LogLine {
  at: string;
  stage: string;
  text: string;
}

export interface SystemEvent {
  id: number;
  at: string;
  actorKind: 'User' | 'Agent' | 'System';
  actorName: string;
  kind: string;
  text: string;
}

export interface GeneralSettings {
  timeZoneId: string;
  defaultModelTimeoutMinutes: number;
  silenceTimeoutMinutes: number;
  logRetentionDays: number;
  recycleAfterModels: number;
  recycleMemoryMb: number;
  ramReserveMb: number;
  ramPerJobMb: number;
}

export interface Dashboard {
  kpi: {
    exported24h: number;
    failed24h: number;
    failedPercent: number;
    queued: number;
    running: number;
    avgDurationSeconds24h: number | null;
    agentsOnline: number;
    agentsTotal: number;
  };
  daily: { date: string; succeeded: number; failed: number }[];
  upcoming: { scheduleId: string; name: string; at: string; modelCount: number }[];
  recentErrors: { at: string; modelName: string; note: string | null; agentName: string | null; revitYear: number | null; jobId: string; runId: string }[];
}

// ── Проверка LOI ──────────────────────────────────────────────────────────────────────────────────────────────

export type ModuleKind = 'export' | 'loi' | 'loi-catalog' | 'clash-extract';

/** Настройки LOI в расписании: null у вида и РН — как в профиле. */
/** Исключение расписания: этой модели (имя без расширения) — этот профиль. */
export interface ModelProfile {
  model: string;
  profileId: string;
}

export interface LoiScheduleSettings {
  /** Один профиль на расписание; null — по разделу модели. */
  profileId: string | null;
  modelProfiles: ModelProfile[];
  /** Профили разделов этого проекта — для «по разделу модели». */
  disciplines: DisciplineProfile[];
  /** Профиль моделей без назначенного раздела; null — они пропускаются. */
  fallbackProfileId: string | null;
  viewName: string | null;
  worksetKeyword: string | null;
  skipLinks: boolean;
  problemsOnly: boolean;
  historyFile: string;
  /** Записывать итоги в аналитику ToadFlow. */
  analytics: boolean;
}

export type ModuleSettings = ExportScheduleSettings | LoiScheduleSettings | ClashScheduleSettings;

export interface DisciplineProfile {
  discipline: string;
  profileId: string;
}

export type ProfileVia = 'Model' | 'Schedule' | 'Discipline' | 'Fallback' | 'None';

export interface LoiRoutingPreviewRow {
  name: string;
  modelPath: string;
  discipline: string | null;
  profileId: string | null;
  profileName: string | null;
  via: ProfileVia;
}

/** Счётчики заполнения: значений проверено, заполнено, пусто, параметра нет. */
interface FillCounts {
  values: number;
  filled: number;
  empty: number;
  missing: number;
  fillRate: number | null;
}

export interface ParameterSummary {
  name: string;
  filled: number;
  empty: number;
  missing: number;
  total: number;
  fillRate: number | null;
}

export interface RuleSummary extends FillCounts {
  title: string;
  elements: number;
  completeElements: number;
  completeness: number | null;
  parameters: ParameterSummary[];
}

/** Итог проверки модели — CheckSummary из ToadParamCheck: тот же формат у заданий сервиса и у плагина ParamChecker. */
export interface LoiJobResult extends FillCounts {
  version: number;
  modelName: string;
  modelPath: string;
  profileName: string;
  profileFingerprint: string;
  viewName: string;
  checkedAt: string;
  durationSeconds: number;
  reportPath: string | null;
  elements: number;
  completeElements: number;
  completeness: number | null;
  rules: RuleSummary[];
}

export interface LoiCategory {
  key: string;
  name: string;
  isModel: boolean;
}

export interface LoiCatalogResult {
  categories: LoiCategory[];
  parameters: string[];
  hasSharedParameterFile: boolean;
}

export type JobResult = ExportJobResult | LoiJobResult | LoiCatalogResult;

export interface LoiProfileSummary {
  id: string;
  name: string;
  /** Группа профиля (обычно проект); пусто — без группы. Имя уникально внутри группы. */
  group: string;
  ruleCount: number;
  viewName: string;
  worksetKeyword: string;
  updatedAt: string;
  updatedBy: string;
  problems: string[];
}

export interface LoiProfileDetails {
  id: string;
  name: string;
  group: string;
  updatedAt: string;
  updatedBy: string;
  profile: CheckProfile;
  problems: string[];
}

export interface LoiCatalogSummary {
  revitYear: number;
  updatedAt: string;
  categories: number;
  parameters: number;
  hasSharedParameterFile: boolean;
}

export interface LoiCatalogOverview {
  catalogs: LoiCatalogSummary[];
  agentYears: number[];
}

export interface LoiCatalogView {
  revitYear: number;
  updatedAt: string;
  catalog: LoiCatalogResult;
}

// Профиль — в формате файлов ParamChecker (Newtonsoft: имена с заглавной буквы, пустые поля опускаются). Так он без
// переделок уходит в плагин и обратно.

export type SelectionMode = 'CategoriesOnly' | 'ParametersOnly' | 'CategoriesOrParameters' | 'CategoriesAndParameters';
export type ConditionLogic = 'And' | 'Or';
export type ConditionOperator =
  | 'Equals' | 'NotEquals' | 'GreaterThan' | 'GreaterThanOrEquals' | 'LessThan' | 'LessThanOrEquals'
  | 'Contains' | 'NotContains' | 'Exists' | 'NotExists' | 'IsFilled' | 'IsEmpty' | 'StartsWith' | 'EndsWith';

/** Условие на параметр или группа условий (Group), связанных через «И». */
export interface Condition {
  Parameter?: string;
  Operator?: ConditionOperator;
  Value?: string;
  Group?: Condition[];
}

export interface RuleFilter {
  Categories: string[];
  Mode: SelectionMode;
  Logic: ConditionLogic;
  Conditions: Condition[];
}

export interface CheckRule {
  Title: string;
  Filter: RuleFilter;
  Parameters: string[];
}

export interface CheckProfile {
  Name: string;
  IsEnabled?: boolean;
  ViewName: string;
  WorksetKeyword: string;
  Models?: unknown[];
  Rules: CheckRule[];
}

// ── Аналитика LOI ─────────────────────────────────────────────────────────────────────────────────────────────

export type LoiCheckSource = 'Schedule' | 'Manual';

export interface AnalyticsSettings {
  enabled: boolean;
  /** 0 — хранить всегда. */
  retentionYears: number;
}

export interface NamingEntry {
  field: string;
  /** Куски имени через «+», например DON+BNK-DOO. */
  tokens: string;
  value: string;
  customer?: string | null;
}

export interface NamingPattern {
  field: string;
  pattern: string;
  value: string;
}

export interface NamingConfig {
  separators: string;
  entries: NamingEntry[];
  patterns: NamingPattern[];
  nameExpressions: string[];
}

export interface ModelClassification {
  customer: string | null;
  object: string | null;
  discipline: string | null;
  part: string | null;
  fields: Record<string, string>;
  unrecognized: string[];
}

export interface NamingPreviewRow {
  name: string;
  result: ModelClassification;
}

export interface KnownModel {
  name: string;
  path: string;
}

export interface ModelOverride {
  modelKey: string;
  modelName: string;
  /** Поправка по имени — для всех моделей с этим именем; иначе modelKey — путь одной модели. */
  byName: boolean;
  customer: string | null;
  object: string | null;
  discipline: string | null;
  part: string | null;
  updatedAt: string;
}

export interface AnalyticsFilters {
  customers: string[];
  objects: { name: string; customer: string | null }[];
  disciplines: string[];
  parts: string[];
  profiles: string[];
  first: string | null;
  last: string | null;
  /** Свои поля справочника и их значения. */
  fields: Record<string, string[]>;
}

export interface GroupStats {
  key: string | null;
  customer: string | null;
  models: number;
  fillRate: number | null;
  completeness: number | null;
}

export interface MatrixCell {
  object: string | null;
  discipline: string | null;
  models: number;
  fillRate: number | null;
}

export interface TrendPoint {
  bucket: string;
  series: string | null;
  checks: number;
  fillRate: number | null;
  completeness: number | null;
}

export interface AnalyticsModelRow {
  modelKey: string;
  modelName: string;
  modelPath: string;
  customer: string | null;
  object: string | null;
  discipline: string | null;
  part: string | null;
  checkedAt: string;
  source: LoiCheckSource;
  profileName: string;
  elements: number;
  fillRate: number | null;
  completeness: number | null;
  previousFillRate: number | null;
  profileChanged: boolean;
  /** Свои поля справочника именования, например «Стадия». */
  fields: Record<string, string>;
}

export interface AnalyticsParameterRow {
  name: string;
  models: number;
  total: number;
  filled: number;
  empty: number;
  missing: number;
  fillRate: number | null;
  worstModels: string[];
}

export interface AnalyticsOverview {
  models: number;
  checks: number;
  manualChecks: number;
  fillRate: number | null;
  completeness: number | null;
  byObject: GroupStats[];
  byDiscipline: GroupStats[];
  matrix: MatrixCell[];
  granularity: 'day' | 'week';
  trend: TrendPoint[];
  trendByObject: TrendPoint[];
  worstModels: AnalyticsModelRow[];
  worstParameters: AnalyticsParameterRow[];
}

export interface ModelHistory {
  model: AnalyticsModelRow;
  checks: { id: string; checkedAt: string; source: LoiCheckSource; userName: string | null; profileName: string; profileFingerprint: string; elements: number; fillRate: number | null; completeness: number | null }[];
  rules: { checkId: string; position: number; title: string; elements: number; fillRate: number | null; completeness: number | null }[];
  parameters: { rule: string; name: string; filled: number; empty: number; missing: number; fillRate: number | null }[];
}

// ── Личные токены ────────────────────────────────────────────────────────────────────────────────────────────

export interface ApiToken {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
}

// ── Коллизии ──────────────────────────────────────────────────────────────────────────────────────────────────

/** Выгрузка геометрии для коллизий в расписании: 3D-вид — фильтр выгружаемых элементов. */
export interface ClashScheduleSettings {
  viewName: string;
  worksetKeyword: string;
  /** Утверждённые части, которые прогоняются после выгрузки. */
  parts: string[];
}

export interface ClashSnapshotView {
  id: string;
  extractedAt: string;
  revitYear: number;
  viewName: string;
  elements: number;
  triangles: number;
  inventory: number;
  geometryBytes: number;
  parametersBytes: number;
  jobId: string;
}

export interface ClashModelView {
  id: string;
  name: string;
  modelPath: string;
  latest: ClashSnapshotView | null;
  snapshotCount: number;
}

export interface CatalogCategory {
  /** Имя BuiltInCategory, например OST_Walls. */
  key: string;
  count: number;
}

export interface CatalogParameter {
  name: string;
  sharedGuid: string | null;
  count: number;
  isNumeric: boolean;
  categories: string[];
}

/** Что встречается в выгруженных элементах моделей — подсказки редактору наборов. */
export interface ModelCatalog {
  categories: CatalogCategory[];
  parameters: CatalogParameter[];
  families: string[];
  types: string[];
  levels: string[];
  worksets: string[];
}

export interface ClashStorageSettings {
  keepSnapshots: number;
}

export interface PartModelView {
  modelId: string;
  name: string;
  modelPath: string;
  parsedDiscipline: string | null;
  disciplineOverride: string | null;
  /** Раздел в проверке: поправка или разбор имени; null — не определён. */
  discipline: string | null;
  latest: ClashSnapshotView | null;
}

export interface ClashPartSummary {
  id: string;
  name: string;
  object: string | null;
  part: string | null;
  models: number;
  sets: number;
  updatedAt: string;
  /** Группы черновика. */
  groups: string[];
  /** «ЭОМ: нет выгрузки», «ВК: выгрузка от 12.10». */
  warnings: string[];
  lastRun: DraftRunSummary | null;
  /** Последняя утверждённая версия; null — часть не утверждалась. */
  approvedVersion: number | null;
  lastApproved: ApprovedRunSummary | null;
}

export interface ApprovedVersionView {
  version: number;
  approvedAt: string;
  userName: string;
  comment: string | null;
  runId: string | null;
}

/** Итог сверки утверждённого прогона: new, returned, fixed, goneByRules, unchecked — переходы в прогоне; active, fake — сколько их у части. */
export interface ApprovedRunSummary {
  found: number;
  new: number;
  active: number;
  returned: number;
  fixed: number;
  goneByRules: number;
  unchecked: number;
  fake: number;
  reusedPairs: number;
}

/** Проект — объект из разбора имён моделей; object = null — модели и части без объекта. */
export interface ClashProjectView {
  object: string | null;
  name: string;
  parts: number;
  models: number;
}

export interface ProjectModelView {
  id: string;
  name: string;
  part: string | null;
  discipline: string | null;
  latest: ClashSnapshotView | null;
  snapshotCount: number;
  /** Выгрузки нет или она старше недели. */
  stale: boolean;
}

export interface ClashPartView {
  id: string;
  name: string;
  object: string | null;
  part: string | null;
  updatedAt: string;
  models: PartModelView[];
}

export interface PartModelRef {
  /** Id модели в форме N — так модель указана в источнике группы. */
  modelKey: string;
  name: string;
  discipline: string | null;
}

export interface DraftView {
  configuration: import('../lib/clashConfig').ClashConfig;
  /** Версия черновика: растёт на каждом сохранении. */
  version: number;
  disciplines: string[];
  models: PartModelRef[];
  problems: string[];
}

/** Элемент, который изменили двое по-разному. */
export interface MergeConflict {
  key: string;
  label: string;
  kind: 'group' | 'set' | 'cell' | 'exclusion' | 'flag';
}

export interface PresenceView {
  userId: string;
  displayName: string;
  tab: string;
  setId: string | null;
}

export interface ValueCount {
  value: string;
  count: number;
}

export interface UnassignedResult {
  count: number;
  byCategory: ValueCount[];
}

export interface ClashTemplateView {
  id: string;
  name: string;
  sets: number;
  groups: string[];
  updatedAt: string;
}

export interface SetPreviewResult {
  matched: number;
  total: number;
  /** Сколько отобрала каждая группа «или». */
  byAnyOf: number[];
  sample: { modelKey: string; uniqueId: string; elementId: number; category: string; family: string | null; type: string | null }[];
}

export type EngineRunState = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';

export interface EngineRunStats {
  elements: number;
  triangles: number;
  candidatePairs: number;
  loadSeconds: number;
  broadSeconds: number;
  narrowSeconds: number;
  peakManagedBytes: number;
  peakWorkingSetBytes: number;
}

export interface DraftRunSummary {
  runId: string;
  state: EngineRunState;
  stage: string | null;
  hitCount: number | null;
  error: string | null;
  createdAt: string;
  finishedAt: string | null;
  stats: EngineRunStats | null;
}

/** Проверка — включённая клетка матрицы прогона; ключ — Id групп в форме N через «|». */
export interface CheckCount {
  key: string;
  groupA: string;
  groupAName: string;
  groupB: string;
  groupBName: string;
  count: number;
  toleranceMm: number;
}

export interface SetPairCount {
  setA: string;
  setAName: string;
  setB: string;
  setBName: string;
  count: number;
}

export interface DraftSummary {
  total: number;
  /** Проверки в порядке матрицы, с нулями. */
  checks: CheckCount[];
  /** Результат прогона до групп — нужно прогнать заново. */
  legacy: boolean;
}

export interface DraftFacets {
  levels: ValueCount[];
  models: ValueCount[];
  categories: ValueCount[];
}

export interface DraftHitView {
  runId: string;
  number: number;
  depthMm: number;
  x: number;
  y: number;
  z: number;
  modelA: string;
  modelNameA: string;
  uniqueIdA: string;
  elementIdA: number;
  disciplineA: string;
  setNamesA: string[];
  categoryA: string;
  familyA: string | null;
  typeA: string | null;
  levelA: string | null;
  modelB: string;
  modelNameB: string;
  uniqueIdB: string;
  elementIdB: number;
  disciplineB: string;
  setNamesB: string[];
  categoryB: string;
  familyB: string | null;
  typeB: string | null;
  levelB: string | null;
  checks: string[];
  /** Пара наборов в проверке запроса: «{набор:N}|{набор:N}». */
  setPairKey: string;
}

export interface HitPage {
  total: number;
  items: DraftHitView[];
}

/** Элемент сцены просмотрщика; габарит — мм относительно точки коллизии. */
export interface ViewerElementView {
  index: number;
  role: 'A' | 'B' | 'context';
  modelId: string;
  modelName: string;
  discipline: string;
  uniqueId: string;
  elementId: number;
  category: string;
  family: string | null;
  type: string | null;
  level: string | null;
  min: [number, number, number];
  max: [number, number, number];
  triangles: number;
  mesh: boolean;
}

export interface ViewerSceneView {
  runId: string;
  number: number;
  x: number;
  y: number;
  z: number;
  radiusMm: number;
  depthMm: number;
  elements: ViewerElementView[];
  meshTriangles: number;
  truncated: boolean;
}

export interface ViewerParameter {
  name: string;
  text: string | null;
  number: number | null;
}

export interface ViewerElementProperties {
  modelName: string;
  discipline: string;
  uniqueId: string;
  elementId: number;
  category: string;
  family: string | null;
  type: string | null;
  level: string | null;
  workset: string | null;
  parameters: ViewerParameter[];
}

export type CollisionStatus = 'New' | 'Active' | 'Returned' | 'Fake' | 'FixedPending' | 'Fixed' | 'GoneByRules' | 'Unchecked';
export type CollisionAssignee = 'Both' | 'SideA' | 'SideB';

/** Последний утверждённый прогон части. */
export interface ApprovedRunView {
  run: DraftRunSummary;
  version: number | null;
  summary: ApprovedRunSummary | null;
}

export interface CollisionSummary {
  /** Открытые: новые, активные, вернувшиеся, отмеченные исправленными. */
  total: number;
  statuses: { status: CollisionStatus; count: number }[];
  checks: CheckCount[];
}

export interface CollisionRow {
  id: string;
  number: number;
  status: CollisionStatus;
  assignee: CollisionAssignee;
  /** Найдена последним прогоном. */
  foundNow: boolean;
  depthMm: number;
  x: number;
  y: number;
  z: number;
  modelA: string;
  modelNameA: string;
  uniqueIdA: string;
  elementIdA: number;
  disciplineA: string;
  categoryA: string;
  familyA: string | null;
  typeA: string | null;
  levelA: string | null;
  modelB: string;
  modelNameB: string;
  uniqueIdB: string;
  elementIdB: number;
  disciplineB: string;
  categoryB: string;
  familyB: string | null;
  typeB: string | null;
  levelB: string | null;
  checks: string[];
  firstVersion: number;
  lastFoundVersion: number;
  suggestedPredecessorId: string | null;
  suggestedPredecessorNumber: number | null;
}

export interface CollisionPage {
  total: number;
  items: CollisionRow[];
}

/** Запись ленты: kind — Note, Status, Assignee, Found, Suggestion, Merged или Depth (образец глубины прогона). */
export interface FeedEntry {
  at: string;
  kind: string;
  userName: string | null;
  text: string;
  fromStatus: CollisionStatus | null;
  toStatus: CollisionStatus | null;
  version: number | null;
  depthMm: number | null;
  /** Автор записи — для карточки коллеги; null — система. */
  userId?: string | null;
}

export interface CollisionCard {
  collision: CollisionRow;
  feed: FeedEntry[];
}

export interface CheckModelView {
  modelId: string;
  name: string;
  discipline: string | null;
}

/** Утверждённая проверка для «Коллизии → Проверки». */
export interface CheckView {
  partId: string;
  name: string;
  object: string | null;
  part: string | null;
  version: number;
  lastRunAt: string | null;
  lastRun: ApprovedRunSummary | null;
  open: number;
  models: CheckModelView[];
}

/** Строка «Моих коллизий»: коллизия, её проверка и проект, кто правит. */
export interface MyCollisionRow {
  collision: CollisionRow;
  partId: string;
  partName: string;
  object: string | null;
  part: string | null;
  /** «Кто правит» — моя сторона или обе; false — взяла другая сторона. */
  mineToFix: boolean;
  takenBy: string | null;
  takenAt: string | null;
}

export interface MyCollisionPage {
  total: number;
  items: MyCollisionRow[];
}

export interface MySummary {
  disciplines: string[];
  toFix: number;
  takenByOther: number;
  newSinceVisit: number;
  previousSeenAt: string | null;
}

/** Контакты коллеги — карточка по имени в ленте коллизии. */
export interface UserCard {
  id: string;
  displayName: string;
  position: string | null;
  email: string | null;
  phone: string | null;
  disciplines: string[];
}
