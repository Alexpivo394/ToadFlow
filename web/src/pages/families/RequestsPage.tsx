import { useNavigate, useSearchParams } from 'react-router';
import { Badge, Box, Button, Center, Group, Image, Indicator, Paper, SegmentedControl, SimpleGrid, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle, IconPhoto, IconPlus } from '@tabler/icons-react';
import { useApi } from '../../api/queries';
import type { Page } from '../../api/types';
import { useImage } from '../../api/useImage';
import { useAuth } from '../../auth/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { Empty } from '../../components/ui';
import { t } from '../../i18n/ru';
import { requestFilePath, requestsKey, type RequestListItem, type RequestState, type RequestStats } from './api';
import { day } from './request/RequestParts';

export const requestStateColor: Record<RequestState, string> = {
  New: 'blue', InWork: 'orange', NeedInfo: 'yellow', Review: 'violet', Done: 'teal', Exists: 'teal', Rejected: 'gray', Withdrawn: 'gray',
};

export function RequestStateBadge({ state }: { state: RequestState }) {
  return <Badge variant="light" color={requestStateColor[state]}>{t.requestState[state]}</Badge>;
}

type Scope = 'mine' | 'direction' | 'assigned' | 'unassigned' | 'needinfo' | 'review' | 'all';

function Thumbnail({ request }: { request: RequestListItem }) {
  const image = useImage(request.thumbnailFileId ? requestFilePath(request.id, request.thumbnailFileId, true) : null);
  if (image.url) return <Image src={image.url} w={44} h={44} radius="sm" fit="cover" alt="" />;
  return <Center w={44} h={44} bg="var(--mantine-color-default-hover)" style={{ borderRadius: 4 }}><IconPhoto size={18} color="var(--mantine-color-dimmed)" /></Center>;
}

function Stat({ value, label, color, active, onClick }: { value: string; label: string; color?: string; active: boolean; onClick: () => void }) {
  return (
    <UnstyledButton onClick={onClick} aria-label={`${value} ${label}`}>
      <Paper withBorder p="sm" radius="md" style={{ borderColor: active ? 'var(--mantine-primary-color-filled)' : undefined }}>
        <Text fz={26} fw={700} c={color} lh={1.1}>{value}</Text>
        <Text size="sm" c="dimmed">{label}</Text>
      </Paper>
    </UnstyledButton>
  );
}

/**
 * Заявки на семейства. Координатору — очередь его направлений с цифрами сверху (каждая — фильтр) и вкладками «Мне назначено», «Без
 * исполнителя», «Ждут автора», «На приёмке», «Все»; проектировщику — «Мои» и «Все». Порядок — серверный: блокирующие выдачу, по сроку,
 * у кого больше ждущих, старшие.
 */
export function RequestsPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const staff = auth.isStaff && auth.can('Operator');
  const defaultScope: Scope = staff ? 'direction' : 'mine';
  const scope = (params.get('scope') as Scope | null) ?? defaultScope;
  const overdueOnly = params.get('overdue') === '1';

  const query = new URLSearchParams({ pageSize: '200', scope });
  if (scope === 'all' && params.get('open') === '1') query.set('state', 'New,InWork,NeedInfo,Review');
  // Пока пользователь не известен, не ясно, какая вкладка по умолчанию — не спрашиваем.
  const requests = useApi<Page<RequestListItem>>([requestsKey, 'list', query.toString()], auth.user ? `/api/families/requests?${query}` : null);
  const stats = useApi<RequestStats>([requestsKey, 'stats'], staff ? '/api/families/requests/stats' : null);
  const go = (next: Record<string, string>) => setParams(next);

  const turnLabel = (r: RequestListItem) => (r.turn === 'author' ? t.requests.turnAuthor : r.turn === 'staff' ? t.requests.turnStaff : null);
  const columns: Column<RequestListItem>[] = [
    { key: 'thumb', title: '', width: 60, render: (r) => <Thumbnail request={r} /> },
    {
      key: 'number', title: '№', width: 70, render: (r) => (
        <Indicator disabled={!r.unread} size={8} offset={-4} position="middle-start" processing>
          <Text size="sm" pl={r.unread ? 10 : 0}>{r.number}</Text>
        </Indicator>
      ), sort: (r) => r.number,
    },
    {
      key: 'title', title: t.requests.what, render: (r) => (
        <Stack gap={2}>
          <Group gap={6} wrap="nowrap">
            {r.blocksIssue && <IconAlertTriangle size={16} color="var(--mantine-color-red-6)" aria-label={t.requests.blocksIssue} />}
            <Text size="sm" fw={r.unread ? 700 : 500} lineClamp={2}>{r.title}</Text>
          </Group>
          <Text size="xs" c="dimmed">
            {[r.kind === 'Fix' ? `${t.requests.fixOf}${r.targetCardName ? ` «${r.targetCardName}»` : ''}` : null, r.directionName, r.project, r.cardName]
              .filter(Boolean).join(' · ')}
          </Text>
        </Stack>
      ), sort: (r) => r.title,
    },
    {
      key: 'state', title: t.requests.state, width: 160, render: (r) => (
        <Stack gap={2}>
          <RequestStateBadge state={r.state} />
          {turnLabel(r) && <Text size="xs" c={r.turn === 'author' ? 'orange' : 'dimmed'}>{turnLabel(r)}</Text>}
        </Stack>
      ), sort: (r) => r.state,
      filter: {
        options: (Object.keys(t.requestState) as RequestState[]).map((s) => ({ value: s, label: t.requestState[s] })),
        match: (r, values) => values.includes(r.state),
      },
    },
    {
      key: 'needed', title: t.requests.neededBy, width: 110, render: (r) => r.neededBy
        ? <Text size="sm" c={r.overdue ? 'red' : undefined} fw={r.overdue ? 700 : undefined} data-overdue={r.overdue ? 'true' : 'false'}>{day(r.neededBy)}</Text>
        : '—', sort: (r) => r.neededBy ?? '9999',
    },
    { key: 'promised', title: t.requests.promisedBy, width: 110, render: (r) => (r.promisedBy ? day(r.promisedBy) : '—'), sort: (r) => r.promisedBy ?? '9999' },
    { key: 'waiting', title: t.requests.waiting, width: 80, render: (r) => `${r.waitingDays} ${t.requests.days}`, sort: (r) => r.waitingDays },
    { key: 'members', title: t.requests.members, width: 70, render: (r) => r.members, sort: (r) => r.members },
    { key: 'assignee', title: t.requests.assignee, render: (r) => r.assigneeName ?? '—', sort: (r) => r.assigneeName },
  ];

  const scopes = staff
    ? [
      { value: 'direction', label: t.requests.scopeDirection },
      { value: 'assigned', label: t.requests.scopeAssigned },
      { value: 'unassigned', label: t.requests.scopeUnassigned },
      { value: 'needinfo', label: t.requests.scopeNeedInfo },
      { value: 'review', label: t.requests.scopeReview },
      { value: 'mine', label: t.requests.mine },
      { value: 'all', label: t.requests.all },
    ]
    : [{ value: 'mine', label: t.requests.mine }, { value: 'all', label: t.requests.all }];

  const s = stats.data;
  const rows = (requests.data?.items ?? []).filter((r) => !overdueOnly || r.overdue);

  return (
    <Stack gap="md">
      {staff && s && (
        <SimpleGrid cols={{ base: 2, sm: 5 }} spacing="sm">
          <Stat value={String(s.mineDirection)} label={t.requests.scopeDirection} active={scope === 'direction' && !overdueOnly} onClick={() => go({})} />
          <Stat value={String(s.open)} label={t.requests.statOpen} active={scope === 'all' && params.get('open') === '1' && !overdueOnly}
            onClick={() => go({ scope: 'all', open: '1' })} />
          <Stat value={String(s.unassigned)} label={t.requests.statUnassigned} active={scope === 'unassigned'} onClick={() => go({ scope: 'unassigned' })} />
          <Stat value={String(s.overdue)} label={t.requests.statOverdue} color={s.overdue > 0 ? 'red' : undefined} active={overdueOnly}
            onClick={() => go({ scope: 'all', open: '1', overdue: '1' })} />
          <Box>
            <Paper withBorder p="sm" radius="md">
              <Text fz={26} fw={700} lh={1.1}>{s.medianCloseHours === null ? '—' : `${Math.round(s.medianCloseHours)} ${t.requests.hours}`}</Text>
              <Text size="sm" c="dimmed">{t.requests.statMedian}</Text>
            </Paper>
          </Box>
        </SimpleGrid>
      )}
      <Group justify="space-between">
        <SegmentedControl data={scopes} value={scope} onChange={(value) => go(value === defaultScope ? {} : { scope: value })} />
        {auth.canWork && (
          <Button leftSection={<IconPlus size={16} />} onClick={() => navigate('/families/requests/new')}>{t.requests.new}</Button>
        )}
      </Group>
      <QueryError query={requests} />
      {requests.data && (
        <DataTable rows={rows} columns={columns} rowKey={(r) => r.id} urlKey="fr" onRowClick={(r) => navigate(`/families/requests/${r.id}`)}
          search={(r) => `${r.number} ${r.title} ${r.project} ${r.authorName} ${r.cardName ?? ''} ${r.directionName ?? ''}`}
          empty={<Empty>{scope === 'mine' ? t.requests.emptyMine : staff ? t.requests.emptyQueue : t.requests.empty}</Empty>} />
      )}
    </Stack>
  );
}
