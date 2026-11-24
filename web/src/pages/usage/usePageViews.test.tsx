import { fireEvent, screen, waitFor } from '@testing-library/react';
import { Link } from 'react-router';
import { describe, expect, it } from 'vitest';
import { Failure, renderUi } from '../../test/renderApp';
import { allSpaces } from '../../spaces/registry';
import { pageOf, usePageViews } from './usePageViews';

function Probe() {
  usePageViews(allSpaces);
  return (
    <>
      <Link to="/clash/checks/123?x=1">проверка</Link>
      <Link to="/clash/checks/123?x=2">другая вкладка</Link>
      <Link to="/usage/waits?tab=user">ожидание</Link>
    </>
  );
}

const modules = (usage: boolean) => [
  { name: 'clash', title: 'Коллизии', enabled: true },
  { name: 'usage', title: 'Аналитика', enabled: usage },
];

function render(usage = true, answer: unknown = null) {
  const sent: unknown[] = [];
  renderUi(<Probe />, {
    '/api/modules': modules(usage),
    'POST /api/usage/pageview': (_url: string, init?: RequestInit) => {
      sent.push(JSON.parse(String(init?.body)));
      return answer;
    },
  }, '/');
  return sent;
}

describe('usePageViews', () => {
  it('finds the menu item the address belongs to', () => {
    expect(pageOf(allSpaces, '/clash/checks/123')).toEqual({ space: 'clash', page: '/clash/checks' });
    expect(pageOf(allSpaces, '/usage')).toEqual({ space: 'usage', page: '/usage' });
    expect(pageOf(allSpaces, '/usage/machines/ws-01')).toEqual({ space: 'usage', page: '/usage/machines' });
    expect(pageOf(allSpaces, '/')).toEqual({ space: 'overview', page: '/' });
    expect(pageOf(allSpaces, '/profile')).toEqual({ space: 'profile', page: '/profile' });
  });

  it('sends a visit per page, not per parameter change', async () => {
    const sent = render();
    await waitFor(() => expect(sent).toEqual([{ space: 'overview', page: '/' }]));

    fireEvent.click(screen.getByText('проверка'));
    await waitFor(() => expect(sent).toHaveLength(2));
    fireEvent.click(screen.getByText('другая вкладка'));
    fireEvent.click(screen.getByText('ожидание'));
    await waitFor(() => expect(sent).toHaveLength(3));

    expect(sent).toEqual([{ space: 'overview', page: '/' }, { space: 'clash', page: '/clash/checks' }, { space: 'usage', page: '/usage/waits' }]);
  });

  it('sends nothing while the module is disabled', async () => {
    const sent = render(false);

    fireEvent.click(await screen.findByText('проверка'));
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(sent).toEqual([]);
  });

  it('a failed request does not break the page', async () => {
    const sent = render(true, new Failure(500, 'сбой'));

    await waitFor(() => expect(sent).toHaveLength(1));
    fireEvent.click(screen.getByText('проверка'));

    await waitFor(() => expect(sent).toHaveLength(2));
    expect(screen.getByText('ожидание')).toBeTruthy();
  });
});
