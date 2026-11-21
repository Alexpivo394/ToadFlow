import { useNavigate, useSearchParams } from 'react-router';
import { Badge, Button, Group, Indicator, SegmentedControl, Stack, Text } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import type { Page } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { Empty } from '../../components/ui';
import { t } from '../../i18n/ru';
import { requestsKey, type RequestListItem, type RequestState } from './api';

const date = (iso: string) => new Date(iso).toLocaleDateString('ru-RU');

export const requestStateColor: Record<RequestState, string> = { New: 'blue', InWork: 'orange', Done: 'teal', Exists: 'teal', Rejected: 'gray' };

export function RequestStateBadge({ state }: { state: RequestState }) {
  return <Badge variant="light" color={requestStateColor[state]}>{t.requestState[state]}</Badge>;
}

type Scope = 'mine' | 'queue' | 'all';

/**
 * Заявки на семейства: «Мои» — поданные и те, к которым присоединился (с отметкой «есть новое»); координатору — «Очередь»
 * (новые и в работе, срочные выше) и «Все».
 */
export function RequestsPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const staff = auth.isStaff && auth.can('Operator');
  const defaultScope: Scope = staff ? 'queue' : 'mine';
  const scope = (params.get('scope') as Scope | null) ?? defaultScope;
  const state = params.get('state');

  const query = new URLSearchParams({ pageSize: '200' });
  if (scope !== 'mine') query.set('scope', 'all');
  if (state) query.set('state', state);
  else if (scope === 'queue') query.set('state', 'New,InWork');
  const requests = useApi<Page<RequestListItem>>([requestsKey, 'list', query.toString()], `/api/families/requests?${query}`);

  const columns: Column<RequestListItem>[] = [
    {
      key: 'number', title: '№', width: 70, render: (r) => (
        <Indicator disabled={!r.unread} size={8} offset={-4} position="middle-start" processing>
          <Text size="sm" pl={r.unread ? 10 : 0}>{r.number}</Text>
        </Indicator>
      ), sort: (r) => r.number,
    },
    {
      key: 'title', title: t.requests.what, render: (r) => (
        <Stack gap={0}>
          <Text size="sm" fw={r.unread ? 700 : 500}>{r.title}</Text>
          {r.cardName && <Text size="xs" c="teal">{r.cardName}</Text>}
        </Stack>
      ), sort: (r) => r.title,
    },
    {
      key: 'state', title: t.requests.state, width: 140, render: (r) => (
        <Group gap={4}>
          <RequestStateBadge state={r.state} />
          {r.urgency === 'Urgent' && <Badge color="red" variant="filled" size="xs">{t.requests.urgent}</Badge>}
        </Group>
      ), sort: (r) => r.state,
      filter: {
        options: (Object.keys(t.requestState) as RequestState[]).map((s) => ({ value: s, label: t.requestState[s] })),
        match: (r, values) => values.includes(r.state),
      },
    },
    { key: 'section', title: t.families.section, width: 100, render: (r) => r.section || '—', sort: (r) => r.section },
    { key: 'project', title: t.requests.project, render: (r) => r.project || '—', sort: (r) => r.project },
    { key: 'author', title: t.requests.author, render: (r) => r.authorName, sort: (r) => r.authorName },
    { key: 'assignee', title: t.requests.assignee, render: (r) => r.assigneeName ?? '—', sort: (r) => r.assigneeName },
    { key: 'members', title: t.requests.members, width: 80, render: (r) => r.members, sort: (r) => r.members },
    { key: 'updated', title: t.requests.updated, width: 110, render: (r) => date(r.updatedAt), sort: (r) => r.updatedAt },
  ];

  const scopes = [
    { value: 'mine', label: t.requests.mine },
    ...(staff ? [{ value: 'queue', label: t.requests.queue }] : []),
    { value: 'all', label: t.requests.all },
  ];

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <SegmentedControl data={scopes} value={scope} onChange={(value) => setParams(value === defaultScope ? {} : { scope: value })} />
        {auth.canWork && (
          <Button leftSection={<IconPlus size={16} />} onClick={() => navigate('/families/requests/new')}>{t.requests.new}</Button>
        )}
      </Group>
      <QueryError query={requests} />
      {requests.data && (
        <DataTable rows={requests.data.items} columns={columns} rowKey={(r) => r.id} urlKey="fr" onRowClick={(r) => navigate(`/families/requests/${r.id}`)}
          search={(r) => `${r.number} ${r.title} ${r.project} ${r.authorName} ${r.cardName ?? ''}`}
          empty={<Empty>{scope === 'mine' ? t.requests.emptyMine : t.requests.empty}</Empty>} />
      )}
    </Stack>
  );
}
