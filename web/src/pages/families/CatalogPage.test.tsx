import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useLocation } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from '../../auth/AuthContext';
import { pickOption, renderUi } from '../../test/renderApp';
import type { FamilyFacets, FamilyHit, FamilyPage } from './api';
import { CatalogPage } from './CatalogPage';

const designer = { id: 'u2', login: 'ivanov', displayName: 'Иванов', role: 'Designer', isDisabled: false, lastLoginAt: null, disciplines: [] };

const hit = (patch: Partial<FamilyHit> = {}): FamilyHit => ({
  id: 'c1', name: 'Клапан обратный', section: 'ОВ', relativePath: 'ОВ\Клапан обратный.rfa', category: 'Арматура трубопроводов', revitYear: 2022,
  typeCount: 14, state: 'Approved', hasPreview: false, updatedAt: '2026-11-20T10:00:00Z', previewHash: null, loadCount: 0, coordinatorsOnly: false, iterationCount: 0, ...patch,
});

const facets: FamilyFacets = {
  sections: [{ value: 'ОВ', count: 2 }, { value: 'КР', count: 1 }],
  categories: [{ value: 'Арматура трубопроводов', count: 2 }],
  years: [{ value: '2022', count: 3 }],
  placements: [{ value: 'OneLevelBased', count: 3 }],
};

const input = (label: string) => screen.getAllByLabelText(label).find((e) => e.tagName === 'INPUT')!;

function Location() {
  return <div data-testid="location">{useLocation().search}</div>;
}

function render(options: { user?: object; hits?: FamilyHit[] } = {}) {
  const searches: string[] = [];
  const page = (): FamilyPage => ({ items: options.hits ?? [hit()], total: (options.hits ?? [hit()]).length });
  renderUi(<AuthProvider><CatalogPage /><Location /></AuthProvider>, {
    ...(options.user ? { '/api/auth/me': options.user } : {}),
    '/api/families/search': (url: string) => {
      searches.push(decodeURIComponent(url));
      return page();
    },
    '/api/families/facets': facets,
  }, '/families/catalog');
  return searches;
}

describe('CatalogPage', () => {
  it('typing a query requests search after a pause and writes q to the address', async () => {
    const searches = render();
    await screen.findByText('Клапан обратный');

    fireEvent.change(screen.getByLabelText('Поиск семейств'), { target: { value: 'клап' } });
    expect(searches.some((url) => url.includes('q=клап'))).toBe(false);

    await waitFor(() => expect(searches.some((url) => url.includes('q=клап'))).toBe(true));
    expect(screen.getByTestId('location').textContent).toContain('q=');
    expect(decodeURIComponent(screen.getByTestId('location').textContent!)).toContain('q=клап');
  });

  it('filters come from facets and are sent to the search', async () => {
    const searches = render();
    await screen.findByText('Клапан обратный');

    await pickOption(input('Раздел'), 'КР (1)');

    await waitFor(() => expect(searches.some((url) => url.includes('section=КР'))).toBe(true));
    await pickOption(input('Размещение'), 'свободное (3)');
    await waitFor(() => expect(searches.some((url) => url.includes('placement=OneLevelBased'))).toBe(true));
  });

  it('designer has "Показывать непроверенные" and no state filter', async () => {
    const searches = render({ user: designer });
    await screen.findByText('Клапан обратный');

    expect(screen.queryAllByLabelText('Состояние')).toHaveLength(0);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Показывать непроверенные' }));

    await waitFor(() => expect(searches.some((url) => url.includes('unreviewed=true'))).toBe(true));
  });

  it('coordinator chooses the state instead', async () => {
    const searches = render();
    await screen.findByText('Клапан обратный');

    expect(screen.queryByRole('checkbox', { name: 'Показывать непроверенные' })).toBeNull();
    await pickOption(input('Состояние'), 'в архиве');

    await waitFor(() => expect(searches.some((url) => url.includes('state=Archived'))).toBe(true));
  });

  it('tile shows a placeholder until the preview exists', async () => {
    render({ hits: [hit(), hit({ id: 'c2', name: 'Кран', state: 'InReview', typeCount: 1 })] });

    expect(await screen.findAllByLabelText('Картинки пока нет')).toHaveLength(2);
    expect(screen.getByText('Арматура трубопроводов · ОВ · 14 типоразмеров')).toBeTruthy();
    expect(screen.getAllByText('на проверке').length).toBeGreaterThan(0);
  });

  it('tile with a preview loads it by the version hash', async () => {
    const fetchSpy = vi.fn();
    render({ hits: [hit({ hasPreview: true, previewHash: 'abc' })] });
    await screen.findByText('Клапан обратный');

    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url) === '/api/families/c1/preview?version=abc')).toBe(true));
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('empty library explains how families appear', async () => {
    render({ hits: [] });

    expect(await screen.findByText(/Положите файл .rfa в папку библиотеки/)).toBeTruthy();
  });
});
