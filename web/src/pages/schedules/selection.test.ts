import { describe, expect, it } from 'vitest';
import type { SourceEntry } from '../../api/types';
import { duplicateNames, emptySelection, fromSelectionDto, isChecked, setFormats, toSelectionDto, toggleFolder, toggleModel } from './selection';

const model = (folder: string, name: string): SourceEntry => ({ name, folderPath: folder, modelPath: `RSN://rs22/${folder}/${name}`, isFolder: false });

describe('selection', () => {
  it('FolderWithIncludeNew_ToFolderDto', () => {
    const state = toggleFolder(emptySelection(), 'ЖК/Корпус1', true, true, []);

    expect(toSelectionDto(state)).toEqual({ models: [], folders: [{ folderPath: 'ЖК/Корпус1', rvt: true, nwc: true }] });
  });

  it('FolderWithoutIncludeNew_ToExplicitModels', () => {
    const loaded = [model('ЖК/Корпус1', 'АР.rvt'), model('ЖК/Корпус1/Секция', 'КР.rvt'), model('ЖК/Корпус2', 'ОВ.rvt')];

    const state = toggleFolder(emptySelection(), 'ЖК/Корпус1', true, false, loaded);

    const dto = toSelectionDto(state);
    expect(dto.folders).toEqual([]);
    expect(dto.models.map((m) => m.name).sort()).toEqual(['АР.rvt', 'КР.rvt']);
  });

  it('UncheckFolder_RemovesFolderAndItsModels', () => {
    const loaded = [model('ЖК/Корпус1', 'АР.rvt')];
    let state = toggleFolder(emptySelection(), 'ЖК/Корпус1', true, false, loaded);
    state = toggleFolder(state, 'ЖК/Корпус1', true, true, loaded);

    state = toggleFolder(state, 'ЖК/Корпус1', false, true, loaded);

    expect(toSelectionDto(state)).toEqual({ models: [], folders: [] });
  });

  it('ExplicitModelOverridesFolderFormats', () => {
    let state = toggleFolder(emptySelection(), 'ЖК', true, true, []);
    const ar = model('ЖК', 'АР.rvt');
    state = toggleModel(state, ar, true);
    state = setFormats(state, ar.modelPath, { rvt: true, nwc: false });

    const dto = toSelectionDto(state);
    expect(dto.folders[0]).toEqual({ folderPath: 'ЖК', rvt: true, nwc: true });
    expect(dto.models[0]).toMatchObject({ modelPath: ar.modelPath, rvt: true, nwc: false });
  });

  it('ModelUnderCheckedFolder_IsChecked', () => {
    const state = toggleFolder(emptySelection(), 'ЖК', true, true, []);

    expect(isChecked(state, model('ЖК/Корпус1', 'АР.rvt'))).toBe(true);
    expect(isChecked(state, model('Школа', 'АР.rvt'))).toBe(false);
  });

  it('DuplicateNames_CaseInsensitive', () => {
    let state = toggleModel(emptySelection(), model('К1', 'АР.rvt'), true);
    state = toggleModel(state, model('К2', 'ар.rvt'), true);
    state = toggleModel(state, model('К2', 'КР.rvt'), true);

    expect(duplicateNames(state)).toEqual(['АР.rvt']);
  });

  it('RoundTrip', () => {
    const dto = {
      models: [{ name: 'АР.rvt', modelPath: 'RSN://rs22/ЖК/АР.rvt', rvt: true, nwc: false }],
      folders: [{ folderPath: 'Школа', rvt: false, nwc: true }],
    };

    expect(toSelectionDto(fromSelectionDto(dto))).toEqual(dto);
  });
});
