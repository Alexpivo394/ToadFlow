import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderUi } from '../../../test/renderApp';
import { ScheduleLine } from './ScheduleLine';

const schedule = (id: string, name: string, selected: boolean, extra: object = {}) => ({
  id, name, selected, isEnabled: true, recurrenceKind: 'Weekly', daysOfWeekMask: 0b1111111, timeOfDay: '02:00', intervalHours: null, onceAt: null,
  nextRunAt: null, ...extra,
});

type FetchMock = { mock: { calls: [string, RequestInit?][] } };
const puts = () => (fetch as unknown as FetchMock).mock.calls.filter(([url, init]) => url === '/api/clash/parts/p1/schedules' && init?.method === 'PUT');

describe('Schedule line', () => {
  it('ScheduleLine_Selected_ShowsNameAndRecurrence', async () => {
    renderUi(<ScheduleLine partId="p1" approved canEdit />, {
      '/api/clash/parts/p1/schedules': [schedule('s1', 'Ночная выгрузка DON', true), schedule('s2', 'Дневная', true, { recurrenceKind: 'EveryNHours', intervalHours: 4 }),
        schedule('s3', 'Чужая', false)],
    });

    const link = await screen.findByRole('link', { name: 'Ночная выгрузка DON' });
    expect(link.getAttribute('href')).toBe('/schedules/s1');
    const line = link.closest('[data-testid="schedule-line"]')!;
    expect(line.textContent).toContain('Прогоняется расписанием');
    expect(line.textContent).toContain('ежедневно 02:00');
    expect(line.textContent).toContain('Дневная');
    expect(line.textContent).toContain('каждые 4 ч');
    expect(line.textContent).not.toContain('Чужая');
  });

  it('ScheduleLine_None_AddPutsSchedule', async () => {
    renderUi(<ScheduleLine partId="p1" approved canEdit />, {
      '/api/clash/parts/p1/schedules': [schedule('s1', 'Ночная', false), schedule('s2', 'Дневная', false)],
    });

    expect(await screen.findByText(/Регулярного прогона нет/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Добавить в расписание/ }));
    fireEvent.click(await screen.findByRole('menuitem', { name: /Дневная/ }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(JSON.parse(String(puts()[0][1]!.body))).toEqual({ scheduleIds: ['s2'] });
  });

  it('ScheduleLine_NotApproved_Hint', async () => {
    renderUi(<ScheduleLine partId="p1" approved={false} canEdit />, { '/api/clash/parts/p1/schedules': [schedule('s1', 'Ночная', false)] });

    expect(await screen.findByText('Регулярный прогон — после утверждения')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Добавить в расписание/ })).toBeNull();
  });
});
