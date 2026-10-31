import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CollisionImage } from './CollisionImage';

describe('CollisionImage', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('CollisionImage_404_ShowsServerReason', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ error: 'Элемент A 123 не найден в слепках прогона' }), { status: 404 })));

    render(<MantineProvider><CollisionImage collisionId="c1" imageVersion="v1" width={160} /></MantineProvider>);

    expect(await screen.findByText('Элемент A 123 не найден в слепках прогона', {}, { timeout: 5000 })).toBeTruthy();
    expect(screen.queryByText('картинка ещё рисуется')).toBeNull();
  });
});
