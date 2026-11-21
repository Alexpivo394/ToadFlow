import type { Page } from '../../api/types';

export type CardState = 'Approved' | 'InReview' | 'Archived';
export type VersionState = 'Approved' | 'InReview' | 'Rejected';
export type ReadState = 'Pending' | 'Done' | 'Failed';
export type IssueKind = 'NameConflict' | 'Duplicate' | 'Ambiguous' | 'Unreadable' | 'UnreviewedChange';

export interface FamilyHit {
  id: string;
  name: string;
  section: string;
  relativePath: string;
  category: string;
  revitYear: number | null;
  typeCount: number;
  state: CardState;
  hasPreview: boolean;
  updatedAt: string;
  /** Хеш действующей версии, если у неё есть картинка: входит в адрес картинки, поэтому её можно кешировать навсегда. */
  previewHash: string | null;
}

export type FamilyPage = Page<FamilyHit>;

export interface FacetValue {
  value: string;
  count: number;
}

export interface FamilyFacets {
  sections: FacetValue[];
  categories: FacetValue[];
  years: FacetValue[];
  placements: FacetValue[];
}

export interface FamilyTypeInfo {
  name: string;
  values: Record<string, string>;
}

export interface FamilyParameterInfo {
  name: string;
  isShared: boolean;
  sharedGuid: string | null;
  isInstance: boolean;
  group: string;
  dataType: string;
}

export interface FamilyContent {
  category: string;
  placement: string;
  revitYear: number;
  types: FamilyTypeInfo[];
  parameters: FamilyParameterInfo[];
  typesFromCatalog: boolean;
}

export interface FamilyVersionView {
  id: string;
  hash: string;
  revitYear: number | null;
  state: VersionState;
  note: string;
  relativePath: string;
  seenAt: string;
  readState: ReadState;
  readError: string | null;
  hasPreview: boolean;
  isCurrent: boolean;
}

export interface FamilyCardView {
  id: string;
  name: string;
  relativePath: string;
  fullPath: string;
  section: string;
  state: CardState;
  category: string;
  placement: string;
  revitYear: number | null;
  typeCount: number;
  description: string | null;
  tags: string[];
  manufacturer: string | null;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
  currentVersionId: string | null;
  currentReadState: ReadState | null;
  currentReadError: string | null;
  hasPreview: boolean;
  content: FamilyContent | null;
  versions: FamilyVersionView[];
}

export interface FamilyNotes {
  description: string | null;
  tags: string[];
  manufacturer: string | null;
}

export interface FamilyReviewItem {
  cardId: string;
  versionId: string;
  name: string;
  relativePath: string;
  isNew: boolean;
  revitYear: number | null;
  seenAt: string;
}

export interface FamilyIssue {
  id: string;
  kind: IssueKind;
  cardId: string | null;
  cardName: string | null;
  relativePath: string;
  text: string;
  createdAt: string;
  resolvedAt: string | null;
}

export interface FamiliesSettings {
  root: string;
  reviewFolder: string;
  scanIntervalMinutes: number;
  libraryAgentId: string | null;
}

export interface LibraryStatus {
  configured: boolean;
  agentName: string | null;
  agentOnline: boolean;
  lastScanAt: string | null;
  lastScanComplete: boolean | null;
  lastScanErrors: string[];
  pendingReads: number;
  failedReads: number;
}

export interface LibraryAgent {
  id: string;
  machineName: string;
  online: boolean;
}

/** Ключ живых обновлений: сервер шлёт события модуля с сущностью «families». */
export const familiesKey = 'families';

/** Адрес картинки: с хешем версии ответ не меняется и кешируется браузером; проектировщику непроверенное — только по своему выбору. */
export function previewPath(cardId: string, hash: string | null, unreviewed: boolean): string | null {
  if (!hash) return null;
  return `/api/families/${cardId}/preview?version=${hash}${unreviewed ? '&unreviewed=true' : ''}`;
}

/** Число типоразмеров словами: «1 типоразмер», «3 типоразмера», «12 типоразмеров». */
export function typesLabel(n: number): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  const word = mod10 === 1 && mod100 !== 11 ? 'типоразмер' : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14) ? 'типоразмера' : 'типоразмеров';
  return `${n} ${word}`;
}
