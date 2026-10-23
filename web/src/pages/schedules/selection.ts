import type { FolderSelection, ModelSelection, Selection, SourceEntry } from '../../api/types';

/** Отметки в дереве источника: явно выбранные модели и папки, из которых модели берутся автоматически. */
export interface SelectionState {
  models: Record<string, ModelSelection>;
  folders: Record<string, FolderSelection>;
}

export const emptySelection = (): SelectionState => ({ models: {}, folders: {} });

const isUnder = (path: string, folder: string) => folder === '' || path === folder || path.startsWith(folder + '/');

export function toggleModel(state: SelectionState, entry: SourceEntry, on: boolean): SelectionState {
  const models = { ...state.models };
  if (on) models[entry.modelPath] = models[entry.modelPath] ?? { name: entry.name, modelPath: entry.modelPath, rvt: true, nwc: true };
  else delete models[entry.modelPath];
  return { ...state, models };
}

/**
 * Отметить папку. С «добавлять новые» — папка целиком, вместе с моделями, которые появятся позже; без — только уже
 * загруженные в дереве модели этой папки и её подпапок. Снятие убирает и папку, и модели внутри неё.
 */
export function toggleFolder(state: SelectionState, folderPath: string, on: boolean, includeNew: boolean, loaded: SourceEntry[]): SelectionState {
  const models = { ...state.models };
  const folders = { ...state.folders };
  const inside = loaded.filter((entry) => !entry.isFolder && isUnder(entry.folderPath, folderPath));

  if (!on) {
    for (const key of Object.keys(folders)) if (isUnder(key, folderPath)) delete folders[key];
    for (const [key, model] of Object.entries(models)) if (isUnder(folderOf(model, loaded), folderPath)) delete models[key];
    return { models, folders };
  }

  if (includeNew) folders[folderPath] = folders[folderPath] ?? { folderPath, rvt: true, nwc: true };
  else for (const entry of inside) models[entry.modelPath] = models[entry.modelPath] ?? { name: entry.name, modelPath: entry.modelPath, rvt: true, nwc: true };
  return { models, folders };
}

export function setFormats(state: SelectionState, path: string, formats: { rvt: boolean; nwc: boolean }): SelectionState {
  if (state.models[path]) return { ...state, models: { ...state.models, [path]: { ...state.models[path], ...formats } } };
  if (state.folders[path]) return { ...state, folders: { ...state.folders, [path]: { ...state.folders[path], ...formats } } };
  return state;
}

/** Отмечена ли модель — явно или через папку с автодобавлением. */
export function isChecked(state: SelectionState, entry: SourceEntry): boolean {
  if (entry.isFolder) return Object.keys(state.folders).some((folder) => isUnder(entry.folderPath, folder));
  return Boolean(state.models[entry.modelPath]) || Object.keys(state.folders).some((folder) => isUnder(entry.folderPath, folder));
}

export function toSelectionDto(state: SelectionState): Selection {
  return { models: Object.values(state.models), folders: Object.values(state.folders) };
}

export function fromSelectionDto(dto: Selection): SelectionState {
  return {
    models: Object.fromEntries(dto.models.map((m) => [m.modelPath, m])),
    folders: Object.fromEntries(dto.folders.map((f) => [f.folderPath, f])),
  };
}

/** Имена явно выбранных моделей, которые встречаются больше одного раза — они перезапишут друг друга в папке результатов. */
export function duplicateNames(state: SelectionState): string[] {
  const seen = new Map<string, { name: string; count: number }>();
  for (const model of Object.values(state.models)) {
    const key = model.name.toLocaleLowerCase('ru');
    const entry = seen.get(key) ?? { name: model.name, count: 0 };
    entry.count++;
    seen.set(key, entry);
  }
  return [...seen.values()].filter((e) => e.count > 1).map((e) => e.name);
}

/** Папка модели: по загруженному дереву, иначе — из пути модели (RSN://сервер/папка/модель.rvt или UNC). */
function folderOf(model: ModelSelection, loaded: SourceEntry[]): string {
  const entry = loaded.find((e) => e.modelPath === model.modelPath);
  if (entry) return entry.folderPath;
  const path = model.modelPath.replace(/^RSN:\/\/[^/]+\//i, '').replace(/\\/g, '/');
  return path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';
}
