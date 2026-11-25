import { useState, type ReactNode } from 'react';
import { Badge, Button, Checkbox, Code, Group, Modal, Paper, Select, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { askConfirm, CopyTextButton, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type FamilyCardView, type FamilyIterationView, type FamilyVersionView } from './api';
import { FileActionButton, useFamilyAction } from './FileActions';

const tx = t.families.iterations;
const date = (iso: string) => new Date(iso).toLocaleDateString('ru-RU');
const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

/**
 * Итерации семейства — проектные варианты со своей историей версий. Координатор создаёт итерацию от любой версии, ставит и снимает финал,
 * правит комментарий и «только координаторы», убирает и возвращает. Версии итерации — со своими комментариями.
 */
export function IterationsPanel({ view, renderVersions }: {
  view: FamilyCardView;
  /** Таблица версий итерации — та же, что у «Основной». */
  renderVersions: (versions: FamilyVersionView[], iteration: FamilyIterationView) => ReactNode;
}) {
  const auth = useAuth();
  const coordinator = auth.isStaff && auth.can('Operator');
  const live = view.iterations.filter((i) => !i.removedAt);
  const removed = view.iterations.filter((i) => i.removedAt);
  const [showRemoved, setShowRemoved] = useState(false);

  return (
    <Stack gap="sm">
      {coordinator && (
        <Group>
          <NewIterationButton view={view} />
        </Group>
      )}
      {view.iterations.length === 0 && <Text size="sm" c="dimmed">{tx.none}</Text>}
      {live.map((iteration) => (
        <IterationCard key={iteration.id} view={view} iteration={iteration} coordinator={coordinator} renderVersions={renderVersions} />
      ))}
      {removed.length > 0 && (
        <>
          <Button size="compact-xs" variant="subtle" onClick={() => setShowRemoved(!showRemoved)}>
            {tx.removed}: {removed.length}
          </Button>
          {showRemoved && removed.map((iteration) => (
            <IterationCard key={iteration.id} view={view} iteration={iteration} coordinator={coordinator} renderVersions={renderVersions} />
          ))}
        </>
      )}
    </Stack>
  );
}

function IterationCard({ view, iteration, coordinator, renderVersions }: {
  view: FamilyCardView;
  iteration: FamilyIterationView;
  coordinator: boolean;
  renderVersions: (versions: FamilyVersionView[], iteration: FamilyIterationView) => ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const versions = view.versions.filter((v) => v.iterationId === iteration.id);
  const unfinal = useApiMutation<void>('DELETE', () => `/api/families/iterations/${iteration.id}/final`, { invalidate: [familiesKey], success: tx.unfinalDone });
  const archive = useFamilyAction(() => `/api/families/iterations/${iteration.id}/archive`, tx.archived);
  const restore = useFamilyAction(() => `/api/families/iterations/${iteration.id}/restore`, tx.restored);
  return (
    <Paper withBorder p="sm" data-testid={`iteration-${iteration.name}`}>
      <Stack gap={6}>
        <Group gap={6} justify="space-between" wrap="nowrap">
          <Group gap={6}>
            <Text fw={600}>{iteration.name}</Text>
            <YearBadge year={iteration.revitYear} />
            {iteration.isFinal && <Badge color="gray" variant="light">{tx.finished}</Badge>}
            {(iteration.coordinatorsOnly || view.coordinatorsOnly) && <Badge color="orange" variant="light" title={tx.locked}>🔒 {tx.coordinatorsOnly}</Badge>}
            {iteration.removedAt && <Badge color="red" variant="light">{tx.removed}</Badge>}
          </Group>
          <Text size="xs" c="dimmed">{date(iteration.updatedAt)}</Text>
        </Group>
        {iteration.comment && <Text size="sm">{iteration.comment}</Text>}
        {iteration.isFinal && (
          <Text size="sm" c="dimmed">
            {tx.finalComment}: {iteration.finalComment}
            {iteration.finalByName && ` — ${iteration.finalByName}`}
            {iteration.finalAt && `, ${date(iteration.finalAt)}`}
          </Text>
        )}
        {iteration.currentVersionId === null && <Text size="sm" c="dimmed">{tx.noFile}</Text>}
        {iteration.fullPath && (
          <Group gap={6} wrap="nowrap">
            <Code style={{ wordBreak: 'break-all', whiteSpace: 'pre-wrap' }}>{iteration.fullPath}</Code>
            <CopyTextButton value={iteration.fullPath} label={t.families.copyPath} copiedLabel={t.families.pathCopied} />
          </Group>
        )}
        <Group gap={4}>
          <Button size="compact-xs" variant="subtle" onClick={() => setOpen(!open)}>{tx.versions} ({versions.length})</Button>
          {coordinator && !iteration.removedAt && (
            <>
              <EditIterationButton iteration={iteration} />
              {!iteration.isFinal && iteration.currentVersionId && <FinalButton iteration={iteration} versions={versions} />}
              {iteration.isFinal && (
                <Button size="compact-xs" variant="subtle" loading={unfinal.isPending}
                  onClick={() => askConfirm(tx.unfinalQuestion(iteration.name), () => unfinal.mutate(), 'blue')}>
                  {tx.unfinal}
                </Button>
              )}
              {iteration.currentVersionId && (
                <FileActionButton color="red" question={tx.archiveQuestion(iteration.name)} loading={archive.isPending} onRun={() => archive.mutate()}>
                  {tx.archive}
                </FileActionButton>
              )}
            </>
          )}
          {coordinator && iteration.removedAt && (
            <FileActionButton loading={restore.isPending} onRun={() => restore.mutate()}>{tx.restore}</FileActionButton>
          )}
        </Group>
        {open && renderVersions(versions, iteration)}
      </Stack>
    </Paper>
  );
}

/** Новая итерация от любой версии семейства: файл кладёт агент библиотеки. */
function NewIterationButton({ view }: { view: FamilyCardView }) {
  const [opened, setOpened] = useState(false);
  const [name, setName] = useState('');
  const [comment, setComment] = useState('');
  const [locked, setLocked] = useState(false);
  const [from, setFrom] = useState<string | null>(view.currentVersionId);
  const create = useFamilyAction<void>(() => `/api/families/${view.id}/iterations`, tx.created,
    () => ({ name: name.trim(), fromVersionId: from, comment: comment.trim() || null, coordinatorsOnly: locked }));
  const iterationName = (id: string | null) => (id ? view.iterations.find((i) => i.id === id)?.name ?? '' : tx.base);
  const options = view.versions.filter((v) => v.state !== 'Rejected').map((v) => ({
    value: v.id,
    label: `${iterationName(v.iterationId)} · ${dateTime(v.seenAt)}${v.comment ? ` · ${v.comment}` : ''}`,
  }));
  return (
    <>
      <FileActionButton onRun={() => setOpened(true)}>{tx.create}</FileActionButton>
      <Modal opened={opened} onClose={() => setOpened(false)} title={`${tx.create} — ${view.name}`}>
        <Stack>
          <TextInput label={tx.name} description={tx.nameHint} value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
          <Select label={tx.from} data={options} value={from} onChange={setFrom} allowDeselect={false} />
          <Textarea label={tx.comment} autosize minRows={2} value={comment} onChange={(e) => setComment(e.currentTarget.value)} />
          <Checkbox label={tx.coordinatorsOnly} description={tx.coordinatorsOnlyHint} checked={locked} onChange={(e) => setLocked(e.currentTarget.checked)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button disabled={name.trim().length === 0 || !from} loading={create.isPending}
              onClick={() => create.mutate(undefined, { onSuccess: () => setOpened(false) })}>{tx.create}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

function EditIterationButton({ iteration }: { iteration: FamilyIterationView }) {
  const [opened, setOpened] = useState(false);
  const [comment, setComment] = useState(iteration.comment ?? '');
  const [locked, setLocked] = useState(iteration.coordinatorsOnly);
  const save = useApiMutation<{ comment: string | null; coordinatorsOnly: boolean }>('PUT', () => `/api/families/iterations/${iteration.id}`,
    { invalidate: [familiesKey], success: tx.saved });
  return (
    <>
      <Button size="compact-xs" variant="subtle" onClick={() => setOpened(true)}>{tx.edit}</Button>
      <Modal opened={opened} onClose={() => setOpened(false)} title={iteration.name}>
        <Stack>
          <Textarea label={tx.comment} autosize minRows={2} value={comment} onChange={(e) => setComment(e.currentTarget.value)} data-autofocus />
          <Checkbox label={tx.coordinatorsOnly} description={tx.coordinatorsOnlyHint} checked={locked} onChange={(e) => setLocked(e.currentTarget.checked)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button loading={save.isPending} onClick={() => save.mutate({ comment: comment.trim() || null, coordinatorsOnly: locked }, { onSuccess: () => setOpened(false) })}>
              {t.common.save}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Финал: обязательный комментарий «что проделано», по умолчанию — действующая версия итерации. */
function FinalButton({ iteration, versions }: { iteration: FamilyIterationView; versions: FamilyVersionView[] }) {
  const [opened, setOpened] = useState(false);
  const [comment, setComment] = useState('');
  const [versionId, setVersionId] = useState<string | null>(iteration.currentVersionId);
  const final = useApiMutation<void>('POST', () => `/api/families/iterations/${iteration.id}/final`,
    { invalidate: [familiesKey], success: tx.finalDone, json: () => ({ versionId, comment: comment.trim() }) });
  return (
    <>
      <Button size="compact-xs" variant="subtle" onClick={() => setOpened(true)}>{tx.final}</Button>
      <Modal opened={opened} onClose={() => setOpened(false)} title={tx.finalTitle(iteration.name)}>
        <Stack>
          <Text size="sm" c="dimmed">{tx.finalHint}</Text>
          <Select label={t.families.tabVersions} allowDeselect={false} value={versionId} onChange={setVersionId}
            data={versions.map((v) => ({ value: v.id, label: `${dateTime(v.seenAt)}${v.comment ? ` · ${v.comment}` : ''}` }))} />
          <Textarea label={tx.finalComment} required autosize minRows={2} value={comment} onChange={(e) => setComment(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button disabled={comment.trim().length === 0 || !versionId} loading={final.isPending}
              onClick={() => final.mutate(undefined, { onSuccess: () => setOpened(false) })}>{tx.final.replace('…', '')}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Флаги семейства: «только координаторы» и «без оповещений» — координатор. */
export function FamilyFlags({ view }: { view: FamilyCardView }) {
  const save = useApiMutation<{ coordinatorsOnly: boolean; silent: boolean }>('PUT', () => `/api/families/${view.id}/flags`,
    { invalidate: [familiesKey], success: tx.flagsSaved });
  return (
    <Group gap="md">
      <Checkbox label={tx.coordinatorsOnly} description={tx.coordinatorsOnlyHint} checked={view.coordinatorsOnly}
        onChange={(e) => save.mutate({ coordinatorsOnly: e.currentTarget.checked, silent: view.silent })} />
      <Checkbox label={tx.silent} description={tx.silentHint} checked={view.silent}
        onChange={(e) => save.mutate({ coordinatorsOnly: view.coordinatorsOnly, silent: e.currentTarget.checked })} />
    </Group>
  );
}
