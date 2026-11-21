import { useEffect, useState } from 'react';
import { Alert, Badge, Button, Code, Drawer, Group, ScrollArea, Stack, Table, Tabs, TagsInput, Text, Textarea, TextInput, Title } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
import { QueryError } from '../../components/QueryState';
import { CopyTextButton, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, typesLabel, type FamilyCardView, type FamilyContent, type FamilyNotes, type FamilyParameterInfo, type FamilyTypeInfo } from './api';
import { FamilyPreview } from './FamilyTile';

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
              <Code block style={{ wordBreak: 'break-all', whiteSpace: 'pre-wrap' }}>{view.fullPath}</Code>
              <Group>
                <CopyTextButton value={view.fullPath} label={t.families.copyPath} copiedLabel={t.families.pathCopied} />
              </Group>
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
              <VersionsTable view={view} />
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

function VersionsTable({ view }: { view: FamilyCardView }) {
  return (
    <Table striped>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t.families.versionSeen}</Table.Th>
          <Table.Th>{t.families.versionNote}</Table.Th>
          <Table.Th>{t.families.versionState}</Table.Th>
          <Table.Th>{t.families.versionRead}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {view.versions.map((version) => (
          <Table.Tr key={version.id}>
            <Table.Td>{dateTime(version.seenAt)}</Table.Td>
            <Table.Td>
              {version.note || '—'}
              {version.isCurrent && <Badge ml={6} size="xs" variant="light">{t.families.current}</Badge>}
            </Table.Td>
            <Table.Td>{t.versionState[version.state]}</Table.Td>
            <Table.Td>
              {t.readState[version.readState]}
              {version.readError && <Text size="xs" c="dimmed">{version.readError}</Text>}
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
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
