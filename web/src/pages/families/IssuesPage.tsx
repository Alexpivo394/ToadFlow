import { useSearchParams } from 'react-router';
import { Alert, Badge, Button, Group, Stack, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { Empty } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type FamilyIssue, type IssueAnswer, type IssueKind } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { FileActionButton, RenameButton, useIssueAnswer } from './FileActions';

const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

const kindColor: Record<IssueKind, string> = {
  NameConflict: 'red',
  Duplicate: 'gray',
  Ambiguous: 'violet',
  Unreadable: 'orange',
  UnreviewedChange: 'yellow',
};

/** Ответы на случай: файлы трогает агент библиотеки, остальное — сервер. Проводником по-прежнему можно, случай закроется сам. */
function IssueActions({ issue }: { issue: FamilyIssue }) {
  const answer = useIssueAnswer();
  const busy = answer.isPending && answer.variables?.id === issue.id;
  const run = (value: IssueAnswer) => answer.mutate({ id: issue.id, answer: value });
  const name = issue.relativePath.split('\\').pop()?.replace(/\.rfa$/i, '') ?? '';
  switch (issue.kind) {
    case 'Duplicate':
      return (
        <Group gap={6} wrap="nowrap">
          <FileActionButton color="red" loading={busy} question={t.families.deleteCopyQuestion(issue.relativePath)} onRun={() => run('delete')}>
            {t.families.deleteCopy}
          </FileActionButton>
          <Button size="compact-xs" variant="default" loading={busy} onClick={(e) => { e.stopPropagation(); run('keep'); }}>{t.families.keepBoth}</Button>
        </Group>
      );
    case 'NameConflict':
      return (
        <Group gap={6} wrap="nowrap">
          <RenameButton issueId={issue.id} current={name} />
          <FileActionButton color="red" loading={busy} question={t.families.deleteFileQuestion(issue.relativePath)} onRun={() => run('delete')}>
            {t.families.deleteFile}
          </FileActionButton>
        </Group>
      );
    case 'Ambiguous':
      return (
        <Group gap={6} wrap="nowrap">
          <Button size="compact-xs" variant="default" loading={busy} onClick={(e) => { e.stopPropagation(); run('same'); }}>{t.families.sameFamily}</Button>
          <Button size="compact-xs" variant="default" loading={busy} onClick={(e) => { e.stopPropagation(); run('other'); }}>{t.families.otherFamily}</Button>
        </Group>
      );
    case 'Unreadable':
      return <Button size="compact-xs" variant="default" loading={busy} onClick={(e) => { e.stopPropagation(); run('retry'); }}>{t.families.readAgain}</Button>;
    default:
      return null;
  }
}

/**
 * «Нужно решение»: что система не может решить сама. У каждого случая — ответы кнопками; исправить можно и в проводнике — тогда случай
 * закроется сам, когда причина исчезнет.
 */
export function IssuesPage() {
  const issues = useApi<FamilyIssue[]>([familiesKey, 'issues'], '/api/families/issues?open=true');
  const [params, setParams] = useSearchParams();
  const open = (cardId: string | null) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (cardId) next.set('card', cardId);
      else next.delete('card');
      return next;
    }, { replace: true });

  const resolve = useApiMutation<string>('POST', (id) => `/api/families/issues/${id}/resolve`, { invalidate: [familiesKey], json: () => undefined });
  const kinds = Object.keys(t.issueKind) as IssueKind[];
  const columns: Column<FamilyIssue>[] = [
    {
      key: 'kind', title: 'Случай', width: 180, render: (issue) => <Badge variant="light" color={kindColor[issue.kind]}>{t.issueKind[issue.kind]}</Badge>,
      sort: (issue) => t.issueKind[issue.kind],
      filter: { options: kinds.map((kind) => ({ value: kind, label: t.issueKind[kind] })), match: (issue, values) => values.includes(issue.kind) },
    },
    {
      key: 'file', title: 'Файл и что сделать', render: (issue) => (
        <Stack gap={2}>
          <Text size="sm" fw={500} style={{ wordBreak: 'break-all' }}>{issue.relativePath}</Text>
          <Text size="sm" c="dimmed">{issue.text}</Text>
        </Stack>
      ), sort: (issue) => issue.relativePath,
    },
    { key: 'card', title: 'С чем связан', render: (issue) => issue.cardName ?? '—', sort: (issue) => issue.cardName },
    { key: 'created', title: 'Когда', width: 140, render: (issue) => dateTime(issue.createdAt), sort: (issue) => issue.createdAt },
    {
      key: 'action', title: '', width: 300, render: (issue) => issue.kind === 'UnreviewedChange' ? (
        <Button size="compact-xs" variant="default" loading={resolve.isPending && resolve.variables === issue.id}
          onClick={(event) => {
            event.stopPropagation();
            resolve.mutate(issue.id);
          }}>
          {t.families.seen}
        </Button>
      ) : <IssueActions issue={issue} />,
    },
  ];

  return (
    <Stack gap="md">
      <Alert color="blue">{t.families.issuesHint}</Alert>
      <QueryError query={issues} />
      {issues.data && (
        <DataTable rows={issues.data} columns={columns} rowKey={(issue) => issue.id} onRowClick={(issue) => issue.cardId && open(issue.cardId)}
          search={(issue) => `${issue.relativePath} ${issue.cardName ?? ''} ${issue.text}`} empty={<Empty>{t.families.issuesEmpty}</Empty>} />
      )}
      <FamilyCardDrawer cardId={params.get('card')} unreviewed onClose={() => open(null)} />
    </Stack>
  );
}
