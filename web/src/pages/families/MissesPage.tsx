import { Link, useNavigate } from 'react-router';
import { Anchor, Button, Stack, Text } from '@mantine/core';
import { IconPlus } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { Empty, PageTitle } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey } from './api';

export interface MissRow {
  query: string;
  count: number;
  users: number;
  lastAt: string;
  requestId: string | null;
  requestNumber: number | null;
}

/** «Искали и не нашли»: координатору — что пополнить в библиотеке; заявку по запросу — одной кнопкой. */
export function MissesPage() {
  const navigate = useNavigate();
  const misses = useApi<MissRow[]>([familiesKey, 'misses'], '/api/families/misses');
  const columns: Column<MissRow>[] = [
    { key: 'query', title: t.insights.query, render: (m) => <Text size="sm" fw={500}>{m.query}</Text>, sort: (m) => m.query },
    { key: 'count', title: t.insights.times, width: 80, render: (m) => m.count, sort: (m) => m.count },
    { key: 'users', title: t.insights.people, width: 80, render: (m) => m.users, sort: (m) => m.users },
    { key: 'last', title: t.insights.last, width: 150, render: (m) => new Date(m.lastAt).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }), sort: (m) => m.lastAt },
    {
      key: 'request', title: t.insights.request, width: 190, render: (m) => m.requestId
        ? <Anchor component={Link} to={`/families/requests/${m.requestId}`} onClick={(e) => e.stopPropagation()}>№ {m.requestNumber}</Anchor>
        : (
          <Button size="compact-xs" variant="light" leftSection={<IconPlus size={12} />} aria-label={`${t.insights.createRequest}: ${m.query}`}
            onClick={(e) => { e.stopPropagation(); navigate(`/families/requests/new?q=${encodeURIComponent(m.query)}`); }}>
            {t.insights.createRequest}
          </Button>
        ),
    },
  ];
  return (
    <Stack gap="md">
      <PageTitle>{t.insights.missesTitle}</PageTitle>
      <Text size="sm" c="dimmed">{t.insights.missesHint}</Text>
      <QueryError query={misses} />
      {misses.data && <DataTable rows={misses.data} columns={columns} rowKey={(m) => m.query} urlKey="fm" empty={<Empty>{t.insights.missesEmpty}</Empty>} />}
    </Stack>
  );
}
