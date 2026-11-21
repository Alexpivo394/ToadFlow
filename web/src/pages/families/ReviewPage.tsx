import { useSearchParams } from 'react-router';
import { Alert, Badge, Group, Stack } from '@mantine/core';
import { useApi } from '../../api/queries';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { Empty, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type FamilyReviewItem } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { ApproveNewButton, FileActionButton, RejectButton, useFamilyAction } from './FileActions';

const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

/** Новая версия утверждённого семейства: кандидат ложится на место прежнего файла, прежний остаётся в истории. */
function ApproveCandidateButton({ versionId, name }: { versionId: string; name: string }) {
  const approve = useFamilyAction(() => `/api/families/versions/${versionId}/approve`, t.families.approved);
  return (
    <FileActionButton color="teal" question={t.families.approveCandidateQuestion(name)} loading={approve.isPending} onRun={() => approve.mutate()}>
      {t.families.approve}
    </FileActionButton>
  );
}

/**
 * «На проверке»: что лежит в папке проверки — новые семейства и новые версии утверждённых. Утвердить — переложить файл на место
 * (кнопкой или проводником), отклонить — убрать его из папки проверки.
 */
export function ReviewPage() {
  const items = useApi<FamilyReviewItem[]>([familiesKey, 'review'], '/api/families/review');
  const [params, setParams] = useSearchParams();
  const open = (cardId: string | null) =>
    setParams((current) => {
      const next = new URLSearchParams(current);
      if (cardId) next.set('card', cardId);
      else next.delete('card');
      return next;
    }, { replace: true });

  const columns: Column<FamilyReviewItem>[] = [
    { key: 'name', title: 'Семейство', render: (item) => item.name, sort: (item) => item.name },
    {
      key: 'kind', title: 'Что это', render: (item) => (
        <Badge variant="light" color={item.isNew ? 'teal' : 'orange'}>{item.isNew ? t.families.reviewNew : t.families.reviewCandidate}</Badge>
      ), sort: (item) => (item.isNew ? 0 : 1),
      filter: {
        options: [{ value: 'new', label: t.families.reviewNew }, { value: 'candidate', label: t.families.reviewCandidate }],
        match: (item, values) => values.includes(item.isNew ? 'new' : 'candidate'),
      },
    },
    { key: 'path', title: 'Путь', render: (item) => item.relativePath, sort: (item) => item.relativePath },
    { key: 'year', title: 'Revit', render: (item) => <YearBadge year={item.revitYear} />, sort: (item) => item.revitYear },
    { key: 'seen', title: 'Когда появилось', render: (item) => dateTime(item.seenAt), sort: (item) => item.seenAt },
    {
      key: 'actions', title: '', width: 280, render: (item) => (
        <Group gap={6} wrap="nowrap">
          {item.isNew ? <ApproveNewButton cardId={item.cardId} name={item.name} /> : <ApproveCandidateButton versionId={item.versionId} name={item.name} />}
          <RejectButton versionId={item.versionId} name={item.name} />
        </Group>
      ),
    },
  ];

  return (
    <Stack gap="md">
      <Alert color="blue">{t.families.reviewHint}</Alert>
      <QueryError query={items} />
      {items.data && (
        <DataTable rows={items.data} columns={columns} rowKey={(item) => item.versionId} onRowClick={(item) => open(item.cardId)}
          search={(item) => `${item.name} ${item.relativePath}`} empty={<Empty>{t.families.reviewEmpty}</Empty>} />
      )}
      <FamilyCardDrawer cardId={params.get('card')} unreviewed onClose={() => open(null)} />
    </Stack>
  );
}
