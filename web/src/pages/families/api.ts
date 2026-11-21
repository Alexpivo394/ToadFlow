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
  /** Сколько раз загружали в проекты из панели Revit. */
  loadCount: number;
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
  /** Кто опубликовал версию из Revit или дописал к ней описание. */
  authorName: string | null;
  /** Что изменилось — словами автора. */
  comment: string | null;
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
  loadCount: number;
  lastLoadedAt: string | null;
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

/** Могут ли кнопки веба менять файлы библиотеки (агент библиотеки новой версии в сети); если нет — почему. */
export interface FileActionsState {
  available: boolean;
  reason: string | null;
}

export type IssueAnswer = 'delete' | 'keep' | 'rename' | 'same' | 'other' | 'retry';

export type RequestState = 'New' | 'InWork' | 'Done' | 'Exists' | 'Rejected';
export type RequestUrgency = 'Normal' | 'Urgent';

export interface RequestListItem {
  id: string;
  number: number;
  title: string;
  section: string;
  urgency: RequestUrgency;
  project: string;
  state: RequestState;
  authorId: string;
  authorName: string;
  assigneeId: string | null;
  assigneeName: string | null;
  cardId: string | null;
  cardName: string | null;
  members: number;
  /** Текущий пользователь — автор или присоединился. */
  isMember: boolean;
  /** Есть изменения, которых текущий участник не видел. */
  unread: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface RequestMessage {
  id: string;
  authorId: string | null;
  authorName: string | null;
  text: string;
  isEvent: boolean;
  at: string;
}

export interface RequestFile {
  id: string;
  fileName: string;
  size: number;
  contentType: string;
  uploadedByName: string;
  at: string;
}

export interface RequestView {
  request: RequestListItem;
  text: string;
  reason: string | null;
  members: { userId: string; name: string }[];
  messages: RequestMessage[];
  files: RequestFile[];
}

export interface NewRequest {
  title: string;
  text: string;
  section: string;
  urgency: RequestUrgency;
  project: string;
}

export interface SimilarResult {
  families: FamilyHit[];
  requests: RequestListItem[];
}

/** Ключ живых обновлений заявок: сервер шлёт их событиями с сущностью «family-requests». */
export const requestsKey = 'family-requests';

export const openRequest = (state: RequestState) => state === 'New' || state === 'InWork';
