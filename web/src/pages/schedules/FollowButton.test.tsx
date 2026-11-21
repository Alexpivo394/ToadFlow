import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderUi } from '../../test/renderApp';
import { FollowButton } from './FollowButton';

vi.setConfig({ testTimeout: 15_000 });

describe('follow a schedule', () => {
  it('toggles following', async () => {
    const calls: string[] = [];
    let following = ['s1'];
    renderUi(<FollowButton id="s1" />, {
      '/api/schedules/following': () => following,
      'DELETE /api/schedules/s1/follow': () => { calls.push('delete'); following = []; return {}; },
      'POST /api/schedules/s1/follow': () => { calls.push('post'); following = ['s1']; return {}; },
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Слежу' }));
    await waitFor(() => expect(calls).toEqual(['delete']));
    fireEvent.click(await screen.findByRole('button', { name: 'Следить' }));
    await waitFor(() => expect(calls).toEqual(['delete', 'post']));
  });
});
