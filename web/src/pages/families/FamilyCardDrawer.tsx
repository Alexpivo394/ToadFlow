import { useEffect, useState } from 'react';
import { Alert, Anchor, Badge, Button, Checkbox, Code, Drawer, Group, Modal, ScrollArea, Stack, Table, Tabs, TagsInput, Text, Textarea, TextInput, Title } from '@mantine/core';
import { Link } from 'react-router';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { CopyTextButton, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import {
  familiesKey, typesLabel, type FamilyCardView, type FamilyContent, type FamilyNotes, type FamilyParameterInfo, type FamilyTypeInfo, type FamilyVersionView,
} from './api';
import { FamilyPreview } from './FamilyTile';
import { ApproveNewButton, FileActionButton, RejectButton, useFamilyAction } from './FileActions';

const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

/** Карточка семейства в боковой панели: картинка, путь, содержимое действующей версии, версии; координатору — описание и метки. */
export function FamilyCardDrawer({ cardId, unreviewed, onClose }: { cardId: string | null; unreviewed: boolean; onClose: () => void }) {
  const auth = useAuth();
  const designer = !auth.isStaff;
  const path = cardId ? `/api/families/${cardId}${designer && unreviewed ? '?unreviewed=true' : ''}` : null;
  const card = useApi<FamilyCardView>([familiesKey, 'card', cardId, designer && unreviewed], path);
  const view = card.data;
  const currentHash = view?.versions.find((v) => v.isCurrent && v.hasPreview)?.hash ?? null;

  return (
    <Drawer opened={cardId !== null} onClose={onClose} position="right" size="xl" title={view?.name ?? ''} aria-label="Карточка семейства">
      <QueryError query={card} />
      {view && (
        <Stack gap="md">
          <Group align="flex-start" wrap="nowrap" gap="md">
            <FamilyPreview cardId={view.id} hash={currentHash} unreviewed={view.state === 'InReview'} size={180} />
            <Stack gap={6} style={{ minWidth: 0 }}>
              <Group gap={6}>
                <YearBadge year={view.revitYear} />
                {view.state !== 'Approved' && <Badge color={view.state === 'InReview' ? 'orange' : 'gray'} variant="light">{t.familyState[view.state]}</Badge>}
              </Group>
              <Text size="sm">{[view.category, view.placement ? t.placement[view.placement] ?? view.placement : '', view.section].filter(Boolean).join(' · ')}</Text>
              {view.typeCount > 0 && <Text size="sm" c="dimmed">{typesLabel(view.typeCount)}</Text>}
              <Text size="sm" c="dimmed">{view.loadCount > 0 && view.lastLoadedAt ? t.families.loaded(view.loadCount, dateTime(view.lastLoadedAt)) : t.families.neverLoaded}</Text>
              <Code block style={{ wordBreak: 'break-all', whiteSpace: 'pre-wrap' }}>{view.fullPath}</Code>
              <Group gap={6}>
                <CopyTextButton value={view.fullPath} label={t.families.copyPath} copiedLabel={t.families.pathCopied} />
                {auth.isStaff && auth.can('Operator') && <CardActions view={view} />}
              </Group>
              <Group gap={6}>
                <Button size="xs" variant="light" color="orange" component={Link} to={`/families/requests/new?kind=fix&card=${view.id}`}>{t.insights.reportProblem}</Button>
              </Group>
              {view.openFixRequests.length > 0 && (
                <Alert color="orange" variant="light" p="xs" title={t.insights.knownProblems}>
                  <Stack gap={2}>
                    {view.openFixRequests.map((r) => (
                      <Anchor key={r.id} component={Link} to={`/families/requests/${r.id}`} size="sm">№ {r.number} · {r.title}</Anchor>
                    ))}
                  </Stack>
                </Alert>
              )}
              {view.loadedProjects.length > 0 && <Text size="sm" c="dimmed">{t.insights.loadedInto}: {view.loadedProjects.join(', ')}</Text>}
              {view.description && <Text size="sm">{view.description}</Text>}
              {(view.tags.length > 0 || view.manufacturer) && (
                <Group gap={4}>
                  {view.manufacturer && <Badge variant="outline">{view.manufacturer}</Badge>}
                  {view.tags.map((tag) => <Badge key={tag} variant="light">{tag}</Badge>)}
                </Group>
              )}
            </Stack>
          </Group>
          <ContentState view={view} />
          <Tabs defaultValue={view.content ? 'types' : 'versions'} keepMounted={false}>
            <Tabs.List>
              {view.content && <Tabs.Tab value="types">{t.families.tabTypes}</Tabs.Tab>}
              {view.content && <Tabs.Tab value="parameters">{t.families.tabParameters}</Tabs.Tab>}
              <Tabs.Tab value="versions">{t.families.tabVersions}</Tabs.Tab>
            </Tabs.List>
            {view.content && (
              <Tabs.Panel value="types" pt="sm">
                <TypesTable content={view.content} />
              </Tabs.Panel>
            )}
            {view.content && (
              <Tabs.Panel value="parameters" pt="sm">
                <ParametersTable parameters={view.content.parameters} />
              </Tabs.Panel>
            )}
            <Tabs.Panel value="versions" pt="sm">
              <VersionsTable view={view} staff={auth.isStaff && auth.can('Operator')} />
            </Tabs.Panel>
          </Tabs>
          {auth.can('Operator') && auth.isStaff && <NotesEditor view={view} />}
        </Stack>
      )}
    </Drawer>
  );
}

/** Пока ферма не прочитала действующую версию — вместо содержимого объяснение; не смогла — её ошибка. */
function ContentState({ view }: { view: FamilyCardView }) {
  if (view.content) return null;
  if (view.currentReadState === 'Failed')
    return <Alert color="red" title={t.families.readFailed}>{view.currentReadError}</Alert>;
  return <Alert color="blue">{t.families.reading}</Alert>;
}

function TypesTable({ content }: { content: FamilyContent }) {
  if (content.types.length === 0) return <Text size="sm" c="dimmed">{t.families.noTypes}</Text>;
  const keys = [...new Set(content.types.flatMap((type) => Object.keys(type.values)))];
  const columns: Column<FamilyTypeInfo>[] = [
    { key: 'name', title: 'Типоразмер', render: (type) => <Text size="sm" fw={500}>{type.name}</Text>, sort: (type) => type.name },
    ...keys.map((key, index) => ({ key: `v${index}`, title: key, render: (type: FamilyTypeInfo) => type.values[key] ?? '', sort: (type: FamilyTypeInfo) => type.values[key] ?? null })),
  ];
  return (
    <Stack gap="xs">
      {content.typesFromCatalog && <Text size="xs" c="dimmed">{t.families.typesFromCatalog}</Text>}
      <ScrollArea type="auto">
        <DataTable rows={content.types} columns={columns} rowKey={(type) => type.name} urlKey="ft" search={(type) => [type.name, ...Object.values(type.values)].join(' ')} />
      </ScrollArea>
    </Stack>
  );
}

function ParametersTable({ parameters }: { parameters: FamilyParameterInfo[] }) {
  const columns: Column<FamilyParameterInfo>[] = [
    { key: 'name', title: t.families.parameterName, render: (p) => p.name, sort: (p) => p.name },
    { key: 'shared', title: t.families.parameterShared, render: (p) => (p.isShared ? <Text size="xs" title={p.sharedGuid ?? ''}>да</Text> : ''), sort: (p) => (p.isShared ? 1 : 0) },
    { key: 'kind', title: t.families.parameterKind, render: (p) => (p.isInstance ? t.families.instance : t.families.type), sort: (p) => (p.isInstance ? 1 : 0) },
    { key: 'group', title: t.families.parameterGroup, render: (p) => p.group, sort: (p) => p.group },
    { key: 'type', title: t.families.parameterType, render: (p) => p.dataType, sort: (p) => p.dataType },
  ];
  return <DataTable rows={parameters} columns={columns} rowKey={(p) => p.name + (p.sharedGuid ?? '')} urlKey="fp" search={(p) => `${p.name} ${p.group}`} />;
}

/** Кнопки координатора для всей карточки: утвердить новое, убрать в архив, вернуть из архива. */
function CardActions({ view }: { view: FamilyCardView }) {
  const archive = useFamilyAction(() => `/api/families/${view.id}/archive`, t.families.archivedDone);
  const restore = useFamilyAction(() => `/api/families/${view.id}/restore`, t.families.restoredDone);
  return (
    <>
      {view.state === 'InReview' && <ApproveNewButton cardId={view.id} name={view.name} />}
      {view.state !== 'Archived' && (
        <FileActionButton color="red" question={t.families.archiveQuestion(view.name)} loading={archive.isPending} onRun={() => archive.mutate()}>
          {t.families.archive}
        </FileActionButton>
      )}
      {view.state === 'Archived' && (
        <FileActionButton question={t.families.restoreQuestion(view.name, view.relativePath)} loading={restore.isPending} onRun={() => restore.mutate()}>
          {t.families.restore}
        </FileActionButton>
      )}
    </>
  );
}

function VersionsTable({ view, staff }: { view: FamilyCardView; staff: boolean }) {
  return (
    <Table striped>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t.families.versionSeen}</Table.Th>
          <Table.Th>{t.families.versionNote}</Table.Th>
          <Table.Th>{t.families.versionState}</Table.Th>
          <Table.Th>{t.families.versionRead}</Table.Th>
          {staff && <Table.Th />}
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {view.versions.map((version) => (
          <Table.Tr key={version.id}>
            <Table.Td>{dateTime(version.seenAt)}</Table.Td>
            <Table.Td>
              {version.note || (version.comment ? '' : '—')}
              {version.isCurrent && <Badge ml={6} size="xs" variant="light">{t.families.current}</Badge>}
              {version.comment && <Text size="sm">{version.comment}</Text>}
              <Text size="xs" c="dimmed">{version.authorName ?? t.families.authorUnknown}</Text>
            </Table.Td>
            <Table.Td>{t.versionState[version.state]}</Table.Td>
            <Table.Td>
              {t.readState[version.readState]}
              {version.readError && <Text size="xs" c="dimmed">{version.readError}</Text>}
            </Table.Td>
            {staff && (
              <Table.Td>
                <VersionActions view={view} version={version} />
              </Table.Td>
            )}
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

/** Что координатор может сделать с версией: утвердить или отклонить кандидата, вернуть прошлую, дописать описание. */
function VersionActions({ view, version }: { view: FamilyCardView; version: FamilyVersionView }) {
  const approve = useFamilyAction(() => `/api/families/versions/${version.id}/approve`, t.families.approved);
  const revert = useFamilyAction(() => `/api/families/versions/${version.id}/revert`, t.families.reverted);
  const candidate = view.state === 'Approved' && version.state === 'InReview' && !version.isCurrent;
  const newInReview = view.state === 'InReview' && version.isCurrent && version.state === 'InReview';
  const previous = view.state === 'Approved' && !version.isCurrent && version.state === 'Approved';
  return (
    <Group gap={4} wrap="nowrap">
      {candidate && (
        <FileActionButton color="teal" question={t.families.approveCandidateQuestion(view.name)} loading={approve.isPending} onRun={() => approve.mutate()}>
          {t.families.approve}
        </FileActionButton>
      )}
      {(candidate || newInReview) && <RejectButton versionId={version.id} name={view.name} />}
      {previous && (
        <FileActionButton question={t.families.revertQuestion(dateTime(version.seenAt))} loading={revert.isPending} onRun={() => revert.mutate()}>
          {t.families.revert}
        </FileActionButton>
      )}
      <CommentButton version={version} />
    </Group>
  );
}

/** Описание версии задним числом — например, к правке, сделанной проводником; «автор — я» по желанию. */
function CommentButton({ version }: { version: FamilyVersionView }) {
  const [opened, setOpened] = useState(false);
  const [comment, setComment] = useState(version.comment ?? '');
  const [mine, setMine] = useState(version.authorName === null);
  const save = useApiMutation<{ comment: string; mine: boolean }>('PUT', () => `/api/families/versions/${version.id}/comment`,
    { invalidate: [familiesKey], success: t.families.commentSaved });
  return (
    <>
      <Button size="compact-xs" variant="subtle" onClick={() => setOpened(true)}>{t.families.comment}</Button>
      <Modal opened={opened} onClose={() => setOpened(false)} title={t.families.comment}>
        <Stack>
          <Textarea label={t.families.commentLabel} autosize minRows={2} value={comment} onChange={(e) => setComment(e.currentTarget.value)} data-autofocus />
          <Checkbox label={t.families.authorMe} checked={mine} onChange={(e) => setMine(e.currentTarget.checked)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button loading={save.isPending} onClick={() => save.mutate({ comment, mine }, { onSuccess: () => setOpened(false) })}>{t.common.save}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Описание, метки и производитель — их координатор дописывает руками, но не обязан. */
function NotesEditor({ view }: { view: FamilyCardView }) {
  const [notes, setNotes] = useState<FamilyNotes>({ description: view.description, tags: view.tags, manufacturer: view.manufacturer });
  useEffect(() => setNotes({ description: view.description, tags: view.tags, manufacturer: view.manufacturer }), [view.id, view.description, view.tags, view.manufacturer]);
  const save = useApiMutation<FamilyNotes>('PUT', () => `/api/families/${view.id}/notes`, { invalidate: [familiesKey], success: t.families.notesSaved });
  return (
    <Stack gap="xs">
      <Title order={5}>{t.families.notesTitle}</Title>
      <Textarea label={t.families.description} autosize minRows={2} value={notes.description ?? ''}
        onChange={(e) => setNotes({ ...notes, description: e.currentTarget.value })} />
      <TagsInput label={t.families.tags} value={notes.tags} onChange={(tags) => setNotes({ ...notes, tags })} />
      <TextInput label={t.families.manufacturer} value={notes.manufacturer ?? ''} onChange={(e) => setNotes({ ...notes, manufacturer: e.currentTarget.value })} />
      <Group>
        <Button loading={save.isPending} onClick={() => save.mutate(notes)}>{t.common.save}</Button>
      </Group>
    </Stack>
  );
}
