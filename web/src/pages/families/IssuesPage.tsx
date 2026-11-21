import { useSearchParams } from 'react-router';
import { Alert, Badge, Stack, Text } from '@mantine/core';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { Empty } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type FamilyIssue, type IssueKind } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';

const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

const kindColor: Record<IssueKind, string> = {
  NameConflict: 'red',
  Duplicate: 'gray',
  Ambiguous: 'violet',
  Unreadable: 'orange',
  UnreviewedChange: 'yellow',
};

/**
 * «Нужно решение»: что система не может решить сама. Текст случая говорит, что сделать в проводнике; случай закрывается сам,
 * когда причина исчезла. Действий с файлами здесь нет — они появятся на этапе 4.
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
