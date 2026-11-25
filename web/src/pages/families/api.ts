import type { Page } from '../../api/types';

export type CardState = 'Approved' | 'InReview' | 'Archived';
export type VersionState = 'Approved' | 'InReview' | 'Rejected';
export type ReadState = 'Pending' | 'Done' | 'Failed';
export type IssueKind = 'NameConflict' | 'Duplicate' | 'Ambiguous' | 'Unreadable' | 'UnreviewedChange' | 'MisplacedIteration' | 'ChangedAfterFinal';

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
  /** Загружают только координаторы. */
  coordinatorsOnly: boolean;
  /** Сколько незавершённых итераций. */
  iterationCount: number;
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
  /** Итерация; null — «Основная». */
  iterationId: string | null;
  /** Опубликована без оповещения. */
  silent: boolean;
}

/** Итерация семейства — проектный вариант со своей историей версий. */
export interface FamilyIterationView {
  id: string;
  name: string;
  comment: string | null;
  currentVersionId: string | null;
  relativePath: string;
  /** Пусто — проектировщику, если загружают только координаторы. */
  fullPath: string;
  coordinatorsOnly: boolean;
  isFinal: boolean;
  finalVersionId: string | null;
  finalComment: string | null;
  finalByName: string | null;
  finalAt: string | null;
  removedAt: string | null;
  createdAt: string;
  updatedAt: string;
  revitYear: number | null;
  currentHash: string | null;
  previewHash: string | null;
  currentReadState: ReadState | null;
  content: FamilyContent | null;
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
  openFixRequests: { id: string; number: number; title: string }[];
  loadedProjects: string[];
  coordinatorsOnly: boolean;
  silent: boolean;
  iterations: FamilyIterationView[];
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
  autoAcceptDays?: number;
  iterationsFolder?: string;
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

export type RequestState = 'New' | 'InWork' | 'NeedInfo' | 'Review' | 'Done' | 'Exists' | 'Rejected' | 'Withdrawn';
export type RequestUrgency = 'Normal' | 'Urgent';
export type RequestKind = 'New' | 'Fix';
export type RequestResolution = 'Made' | 'Existed';
/** Чей ход: координатора или автора и участников; null — заявка закрыта. */
export type RequestTurn = 'staff' | 'author' | null;

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
  kind: RequestKind;
  directionId: string | null;
  directionName: string | null;
  neededBy: string | null;
  promisedBy: string | null;
  blocksIssue: boolean;
  overdue: boolean;
  turn: RequestTurn;
  waitingDays: number;
  thumbnailFileId: string | null;
  revitYear: number | null;
  targetCardId: string | null;
  targetCardName: string | null;
  resolution: RequestResolution | null;
  reviewSince: string | null;
}

export interface RequestMessage {
  id: string;
  authorId: string | null;
  authorName: string | null;
  text: string;
  isEvent: boolean;
  at: string;
  fileIds: string[];
}

export interface RequestFile {
  id: string;
  fileName: string;
  size: number;
  contentType: string;
  uploadedByName: string;
  at: string;
  messageId: string | null;
  uploadedBy: string;
  inline: boolean;
}

export interface RequestView {
  request: RequestListItem;
  text: string;
  reason: string | null;
  members: { userId: string; name: string }[];
  messages: RequestMessage[];
  files: RequestFile[];
  autoAcceptAt: string | null;
}

export interface NewRequest {
  title: string;
  text: string;
  section: string;
  project: string;
  kind: RequestKind;
  directionId: string | null;
  neededBy: string | null;
  blocksIssue: boolean;
  revitYear: number | null;
  targetCardId: string | null;
  fileIds: string[];
}

export interface RequestEdit {
  title: string;
  text: string;
  section: string;
  project: string;
  neededBy: string | null;
  blocksIssue: boolean;
  revitYear: number | null;
}

export interface SimilarResult {
  families: FamilyHit[];
  requests: RequestListItem[];
}

export interface Direction {
  id: string;
  name: string;
  sections: string[];
  disciplines: string[];
  coordinators: { id: string; name: string }[];
  hint: string;
}

export interface RequestStats {
  open: number;
  overdue: number;
  unassigned: number;
  medianCloseHours: number | null;
  mineDirection: number;
}

/** Ключ живых обновлений заявок: сервер шлёт их событиями с сущностью «family-requests». */
export const requestsKey = 'family-requests';

export const openRequest = (state: RequestState) => state === 'New' || state === 'InWork' || state === 'NeedInfo' || state === 'Review';

/** Адрес файла заявки: <c>inline</c> — для показа в браузере. */
export const requestFilePath = (requestId: string, fileId: string, inline = false) =>
  `/api/families/requests/${requestId}/files/${fileId}${inline ? '?inline=1' : ''}`;
