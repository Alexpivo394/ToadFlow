import { act, screen } from '@testing-library/react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { liveHub, renderUi } from '../test/renderApp';
import { useLiveUpdates } from './useLiveUpdates';

let client: QueryClient;
function Probe() {
  client = useQueryClient();
  useLiveUpdates(true);
  return <span>подключено</span>;
}

const invalidated = (spy: { mock: { calls: unknown[][] } }) =>
  spy.mock.calls.map(([filters]) => JSON.stringify((filters as { queryKey?: unknown } | undefined)?.queryKey ?? 'всё'));

describe('Live updates', () => {
  beforeEach(() => liveHub.reset());
  afterEach(() => vi.useRealTimers());

  async function connect() {
    renderUi(<Probe />, {});
    await screen.findByText('подключено');
    await act(async () => {});
    return vi.spyOn(client, 'invalidateQueries');
  }

  /** Регрессия этапа 8: событие коллизий снова обновляет списки. */
  it('Live_CollisionsChanged_InvalidatesClashList', async () => {
    const spy = await connect();

    act(() => liveHub.handlers.changed('clash-collisions', 'p1'));

    expect(invalidated(spy)).toEqual(expect.arrayContaining(['["clash-list"]', '["clash-my"]', '["clash-checks"]', '["clash-collisions"]']));
  });

  it('Live_RunProgress_DoesNotRefetchLists', async () => {
    const spy = await connect();

    act(() => liveHub.handlers.changed('clash-run-progress', 'p1'));

    expect(invalidated(spy)).toEqual(['["clash-parts","p1","run"]', '["clash-collisions","p1","runs"]']);
  });

  it('Live_FirstStartFails_Retries', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    liveHub.failStarts = 2;
    renderUi(<Probe />, {});
    await act(async () => {});
    expect(liveHub.starts).toBe(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_500);
    });

    expect(liveHub.starts).toBe(3);
  });

  /** F5 или обрыв: события за время без связи потеряны — после переподключения перечитывается всё. */
  it('Live_Reconnected_InvalidatesAll', async () => {
    const spy = await connect();

    act(() => liveHub.reconnected?.());

    expect(invalidated(spy)).toEqual(['"всё"']);
  });

});
