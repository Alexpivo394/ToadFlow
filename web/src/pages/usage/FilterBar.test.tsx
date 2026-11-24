import { screen, waitFor } from '@testing-library/react';
import { useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { pickOption, renderUi } from '../../test/renderApp';
import type { UsageFilters } from './api';
import { FilterBar } from './FilterBar';

const filters: UsageFilters = {
  people: [{ id: 'u1', name: 'Иванов', disciplines: ['АР'] }],
  machines: [{ key: 'ws-01', name: 'WS-01' }],
  disciplines: ['АР', 'Без раздела'],
  revitYears: [2022, 2024],
};

function Where() {
  return <div data-testid="where">{useLocation().search}</div>;
}

describe('FilterBar', () => {
  it('shows what the screen is built on', async () => {
    renderUi(<FilterBar basis={{ people: 21, machines: 5 }} />, { '/api/usage/filters': filters });

    expect(await screen.findByText('Построено по 21 человеку и 5 машинам')).toBeTruthy();
  });

  it('choosing a person puts it into the address', async () => {
    renderUi(<><FilterBar /><Where /></>, { '/api/usage/filters': filters }, '/usage/waits');
    const person = (await screen.findAllByLabelText('Человек')).find((e) => e.tagName === 'INPUT')!;
    // Варианты приходят запросом: выбираем, когда список уже есть.
    await waitFor(() => pickOption(person, 'Иванов'));

    await waitFor(() => expect(screen.getByTestId('where').textContent).toBe('?user=u1'));
  });

  it('coverage shows only the discipline filter', async () => {
    renderUi(<FilterBar periodless />, { '/api/usage/filters': filters });

    expect((await screen.findAllByLabelText('Раздел')).length).toBeGreaterThan(0);
    expect(screen.queryAllByLabelText('Человек')).toHaveLength(0);
    expect(screen.queryByRole('radiogroup')).toBeNull();
  });
});
