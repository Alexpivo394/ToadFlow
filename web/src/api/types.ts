// Повторяет DTO сервера (camelCase, перечисления строками). Меняется вместе с сервером.

export type UserRole = 'Viewer' | 'Operator' | 'Admin';
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
  moduleSettings: ExportScheduleSettings;
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
  moduleSettings: ExportScheduleSettings;
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
  result: ExportJobResult | null;
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
