import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { emptyConfig, newGroup } from '../../../lib/clashConfig';
import { batch, configuration, draft, partResponses } from '../../../test/clashPart';
import { renderApp } from '../../../test/renderApp';
import { valuesKey } from './SetEditor';

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const calls = () => (fetch as unknown as FetchMock).mock.calls;

async function savedBody() {
  fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
  await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/draft' && init?.method === 'PUT')).toBe(true));
  const [, init] = calls().find(([url, i]) => url === '/api/clash/parts/p1/draft' && i?.method === 'PUT')!;
  return JSON.parse(String(init!.body)).configuration;
}

describe('Sets tab', () => {
  it('SetsTab_CustomGroupWithSource', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', partResponses);
    fireEvent.click(await screen.findByRole('button', { name: '+ Своя группа' }));
    const dialog = await screen.findByRole('dialog');

    fireEvent.change(within(dialog).getByLabelText('Имя группы'), { target: { value: 'Проезды' } });
    fireEvent.click(within(dialog).getByLabelText('Элементы из моделей разделов'));
    fireEvent.click(await screen.findByRole('option', { name: 'АР' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Добавить' }));

    expect(await screen.findByText('Наборы «Проезды»')).toBeTruthy();
    const config = await savedBody();
    expect(config.groups.at(-1)).toMatchObject({ name: 'Проезды', source: { disciplines: ['АР'], modelKeys: [] } });
  });

  it('SetsTab_OneOfWithPickedValues', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', partResponses);
    await screen.findByLabelText('Имя набора');

    fireEvent.click(screen.getAllByLabelText('Оператор')[0]);
    fireEvent.click(await screen.findByRole('option', { name: 'одно из' }));
    fireEvent.click((await screen.findAllByLabelText('Значения'))[0]);
    fireEvent.click(await screen.findByRole('option', { name: /Стены · 5\s902/ }));
    fireEvent.click(await screen.findByRole('option', { name: /Двери · 310/ }));

    const config = await savedBody();
    expect(config.sets[0].anyOf[0].all[0]).toMatchObject({ operator: 'oneOf', values: ['OST_Walls', 'OST_Doors'] });
  });

  it('SetsTab_ModelCondition_PicksFileByName', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', {
      ...partResponses,
      '/api/clash/parts/p1/values': [{ value: 'm1', count: 5902 }],
    });
    await screen.findByLabelText('Имя набора');

    fireEvent.click(screen.getAllByLabelText('Свойство')[0]);
    fireEvent.click(await screen.findByRole('option', { name: 'Модель (файл)' }));
    fireEvent.click(screen.getAllByLabelText('Оператор')[0]);
    expect(await screen.findByRole('option', { name: 'одно из' })).toBeTruthy();
    expect(screen.queryByRole('option', { name: 'содержит' })).toBeNull(); // «содержит» по ключу модели бессмысленно
    fireEvent.click(await screen.findByRole('option', { name: 'равно' }));
    fireEvent.click((await screen.findAllByLabelText('Значение'))[0]);
    fireEvent.click(await screen.findByRole('option', { name: /POL_K1_AR\.rvt · 5\s902/ }));

    const config = await savedBody();
    expect(config.sets[0].anyOf[0].all[0]).toMatchObject({ property: { kind: 'model' }, operator: 'equals', value: 'm1' });
  });

  it('SetsTab_GroupOfRemovedDiscipline_CanBeDeleted', async () => {
    const itp = { id: 'g-itp', name: 'ИТП', source: { disciplines: ['ИТП'], modelKeys: [] } };
    const pipes = { id: 's-itp', groupId: 'g-itp', name: 'Трубы ИТП', anyOf: [{ all: [{ property: { kind: 'category' }, operator: 'isNotEmpty', value: null }] }] };
    renderApp('/clash/setup/parts/p1?tab=sets', {
      ...partResponses,
      '/api/clash/parts/p1/draft': draft(1, { ...configuration, groups: [...configuration.groups, itp], sets: [...configuration.sets, pipes] }),
    });

    fireEvent.click(await screen.findByText('ИТП'));
    expect(screen.getByText('раздела нет в части')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Удалить группу' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Да' }));

    const config = await savedBody();
    expect(config.groups.map((g: { name: string }) => g.name)).toEqual(['АР', 'ОВ']);
    expect(config.sets.map((s: { name: string }) => s.name)).toEqual(['Стены', 'Воздуховоды']);
  });

  it('SetsTab_GroupOfPresentDiscipline_ExplainsHowToExclude', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', partResponses);
    expect(await screen.findByText(/уберите его модели на вкладке «Модели»/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Удалить группу' })).toBeNull();
  });

  it('SetsTab_UnassignedWarning', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', {
      ...partResponses,
      'POST /api/clash/parts/p1/draft/sets/preview/batch': batch({ count: 312, byCategory: [{ value: 'OST_Furniture', count: 300 }, { value: 'OST_Something', count: 12 }] }),
    });

    expect(await screen.findByText(/312 элементов источника не попали/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать по категориям' }));
    expect(screen.getByText('Мебель — 300')).toBeTruthy();
    expect(screen.getByText('OST_Something — 12')).toBeTruthy();
  });

  it('SetsTab_UnassignedHiddenForCustomGroup', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', {
      ...partResponses,
      'POST /api/clash/parts/p1/draft/sets/preview/batch': batch({ count: 31375, byCategory: [{ value: 'OST_Walls', count: 31375 }] }),
    });
    expect(await screen.findByText(/элементов источника не попали/)).toBeTruthy();

    // Своя группа берёт из источника лишь часть — «не попали» для неё не ошибка.
    fireEvent.click(screen.getByRole('button', { name: '+ Своя группа' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Имя группы'), { target: { value: 'Проезды' } });
    fireEvent.click(within(dialog).getByLabelText('Элементы из моделей разделов'));
    fireEvent.click(await screen.findByRole('option', { name: 'АР' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Добавить' }));
    await screen.findByText('Наборы «Проезды»');

    expect(screen.queryByText(/элементов источника не попали/)).toBeNull();
  });

  it('SetsTab_CountsPerOrGroup', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', partResponses);

    expect(await screen.findByText('попало 14')).toBeTruthy();
    expect(screen.getByText(/В набор попало/)).toBeTruthy();
    await waitFor(() => expect(calls().some(([url, init]) => url === '/api/clash/parts/p1/preview' && JSON.parse(String(init?.body)).setId === 's-walls'))
      .toBe(true));
  });

  /** Счётчики наборов группы — один запрос на версию черновика, а не по запросу на набор; открытый набор — ещё один. */
  it('Sets_OneBatchRequestPerConfigVersion', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', partResponses);
    const batches = () => calls().filter(([url]) => url === '/api/clash/parts/p1/draft/sets/preview/batch');
    const singles = () => calls().filter(([url]) => url === '/api/clash/parts/p1/preview');

    await screen.findByLabelText('Имя набора');
    await waitFor(() => expect(batches()).toHaveLength(1));
    expect(JSON.parse(String(batches()[0][1]!.body))).toMatchObject({ setIds: ['s-walls'], groupId: 'g-ar' });
    expect(singles().length).toBeLessThanOrEqual(1);

    fireEvent.change(screen.getByLabelText('Имя набора'), { target: { value: 'Стены наружные' } });
    await waitFor(() => expect(batches()).toHaveLength(2), { timeout: 2000 });
    await new Promise((resolve) => setTimeout(resolve, 600));
    expect(batches()).toHaveLength(2);
    expect(singles().length).toBeLessThanOrEqual(2);
  });

  it('ValuePicker_KeyIncludesGroupSource', () => {
    const group = newGroup('Проезды', { disciplines: ['АР'], modelKeys: [] });
    const config = { ...emptyConfig(), groups: [group] };
    const moved = { ...config, groups: [{ ...group, source: { disciplines: ['КР'], modelKeys: [] } }] };
    const property = { kind: 'category' as const };

    expect(valuesKey('p1', config, group.id, property)).not.toEqual(valuesKey('p1', moved, group.id, property));
    expect(valuesKey('p1', config, group.id, property)).toEqual(valuesKey('p1', { ...config, sets: [] }, group.id, property));
  });

  /** Наблюдатель настройку части не видит — его уводит в «Проверки». */
  it('SetsTab_ViewerHasNoSetup', async () => {
    renderApp('/clash/setup/parts/p1?tab=sets', {
      ...partResponses,
      '/api/auth/me': { id: 'u2', login: 'viewer', displayName: 'Наблюдатель', role: 'Viewer', isDisabled: false, lastLoginAt: null },
    });

    expect(await screen.findByRole('heading', { name: 'Проверки' })).toBeTruthy();
    expect(screen.queryByLabelText('Имя набора')).toBeNull();
  });
});

vi.setConfig({ testTimeout: 15_000 });
