/** Ответы модуля «Аналитика» (GET /api/usage/...). */

export interface UsageBasis {
  people: number;
  machines: number;
}

export interface UsagePerson {
  id: string;
  name: string;
}

export interface UsageMachineRef {
  key: string;
  name: string;
}

export interface UsageFilters {
  people: { id: string; name: string; disciplines: string[] }[];
  machines: UsageMachineRef[];
  disciplines: string[];
  revitYears: number[];
}

export type WaitGroup = 'model' | 'machine' | 'user' | 'server' | 'link';
export type OperationKind = 'Open' | 'Link' | 'Sync' | 'Save' | 'Reload';

export interface WaitRow {
  key: string;
  title: string;
  count: number;
  medianMs: number | null;
  p90Ms: number | null;
  totalMs: number;
  failed: number;
  /** Медиана по неделям периода; null — в неделю операций не было. */
  trend: (number | null)[];
}

export interface WaitsView {
  basis: UsageBasis;
  rows: WaitRow[];
}

export interface WaitCase {
  id: string;
  kind: OperationKind;
  startedAt: string;
  durationMs: number;
  outcome: 'Succeeded' | 'Failed' | 'Cancelled';
  user: UsagePerson;
  machine: UsageMachineRef;
  modelName: string;
  modelPath: string;
  hostModel: string | null;
  freeMemoryMb: number | null;
  fileSizeBytes: number | null;
}

export interface MachineRow {
  key: string;
  name: string;
  cpu: string | null;
  cores: number | null;
  ramMb: number | null;
  gpu: string | null;
  gpuMemoryMb: number | null;
  diskKind: 'Ssd' | 'Hdd' | 'Unknown';
  diskTotalGb: number | null;
  diskFreeGb: number | null;
  os: string | null;
  revitYears: number[];
  toadTools: string | null;
  lastSeenAt: string | null;
  lastUser: UsagePerson | null;
  openMedianMs: number | null;
  syncMedianMs: number | null;
  sessions: number;
  abnormal: number;
  activeHours: number;
}

export interface MachinesView {
  basis: UsageBasis;
  rows: MachineRow[];
}

export interface MachineCard extends MachineRow {
  firstSeenAt: string | null;
  users: { id: string; name: string; sessions: number }[];
  abnormalSessions: { id: string; startedAt: string; seenAt: string; user: UsagePerson }[];
  weeks: { start: string; openMedianMs: number | null; syncMedianMs: number | null }[];
}

export type CoverageState = 'Never' | 'Active' | 'Gone';

export interface CoverageRow {
  user: { id: string; name: string; role: string; disciplines: string[]; position: string | null };
  state: CoverageState;
  toadTools: string | null;
  outdated: boolean;
  lastSessionAt: string | null;
  machines: string[];
}

export interface CoverageView {
  minToadTools: string;
  latestToadTools: string | null;
  rows: CoverageRow[];
}

export type CommandSource = 'ToadTools' | 'Revit' | 'Addin';

export interface CommandRow {
  source: CommandSource;
  commandId: string;
  name: string;
  tab: string | null;
  count: number;
  people: number;
  failed: number;
  cancelled: number;
  avgMs: number | null;
}

export interface CommandsView {
  basis: UsageBasis;
  ribbonOnly: boolean;
  rows: CommandRow[];
  unused: { commandId: string; name: string; lastRunAt: string | null }[];
}

export interface CommandCard {
  name: string;
  weeks: { start: string; count: number; people: number }[];
  users: { id: string; name: string; count: number }[];
}

export interface ServiceView {
  basis: UsageBasis;
  web: {
    days: { day: string; people: number }[];
    spaces: { space: string; visits: number; people: number }[];
    pages: { space: string; page: string; visits: number; people: number }[];
    roles: { role: string; people: number; visits: number }[];
  };
  plugins: {
    areas: { area: string; calls: number; people: number }[];
    users: { id: string; name: string; areas: { area: string; calls: number }[] }[];
  };
}

export interface SummaryTotals {
  waitMs: number;
  abnormalSessions: number;
  people: number;
  toadToolsRuns: number;
}

export interface SummaryView {
  basis: UsageBasis;
  totals: SummaryTotals;
  previous: SummaryTotals;
  days: { day: string; openMs: number; linkMs: number; syncMs: number; saveMs: number }[];
  slowModels: { key: string; title: string; medianMs: number; count: number }[];
  slowMachines: { key: string; name: string; totalMs: number }[];
  crashingMachines: { key: string; name: string; abnormal: number }[];
}

export interface PersonView {
  user: { id: string; name: string; role: string; position: string | null; disciplines: string[] };
  machines: UsageMachineRef[];
  days: { day: string; activeSeconds: number; sessions: number }[];
  waits: { totalMs: number; rows: { kind: OperationKind; count: number; medianMs: number | null }[] };
  commands: { source: CommandSource; commandId: string; name: string; count: number }[];
  web: { space: string; visits: number }[];
  plugins: { area: string; calls: number }[];
}

export interface UsageSettings {
  retentionMonths: number;
}
