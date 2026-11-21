import { useState } from 'react';
import { Alert, Badge, Button, Card, Divider, Group, Modal, Paper, Stack, Text, Textarea, ThemeIcon } from '@mantine/core';
import { IconAlertTriangle, IconCheck, IconClock, IconHourglass, IconUser } from '@tabler/icons-react';
import { useApiMutation } from '../../../api/queries';
import { t } from '../../../i18n/ru';
import { requestsKey, familiesKey, type RequestFile, type RequestListItem, type RequestView } from '../api';
import { AttachmentGrid, type GridItem } from '../attachments/AttachmentGrid';
import { kindOf } from '../attachments/kinds';
import { Linkify } from '../attachments/Linkify';
import { RequestStateBadge } from '../RequestsPage';

export const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });

/** «2026-12-01» → «01.12.2026». */
export const day = (value: string | null) => (value ? value.slice(0, 10).split('-').reverse().join('.') : '');

export const gridItems = (requestId: string, files: RequestFile[]): GridItem[] =>
  files.map((f) => ({
    key: f.id, id: f.id, name: f.fileName, size: f.size, kind: f.inline ? kindOf(f.contentType, f.fileName) : 'other',
    path: `/api/families/requests/${requestId}/files/${f.id}?inline=1`, busy: false,
  }));

/** Чей ход — крупно: «Ваш ход», «Ход автора», «Ход координатора». */
export function TurnLine({ request, me }: { request: RequestListItem; me: string | undefined }) {
  if (!request.turn) return null;
  const mine = request.turn === 'author' ? request.isMember && request.assigneeId !== me : request.assigneeId === me;
  const label = mine ? t.requests.turnAuthorMine : request.turn === 'author' ? t.requests.turnAuthor : t.requests.turnStaff;
  return (
    <Group gap="xs">
      <ThemeIcon variant="light" color={mine ? 'orange' : 'gray'} radius="xl"><IconHourglass size={16} /></ThemeIcon>
      <Text fw={700} c={mine ? 'orange' : undefined}>{label}</Text>
    </Group>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Group justify="space-between" gap="xs" wrap="nowrap" align="flex-start">
      <Text size="sm" c="dimmed">{label}</Text>
      <Text size="sm" ta="right" component="div">{children}</Text>
    </Group>
  );
}

/** Карточка состояния справа: статус, чей ход, сроки, люди, проект; ниже — действия. */
export function StatusCard({ view, me, onOpenCard, actions }: { view: RequestView; me: string | undefined; onOpenCard: (id: string) => void; actions: React.ReactNode }) {
  const r = view.request;
  const others = view.members.filter((m) => m.userId !== r.authorId);
  return (
    <Card withBorder radius="md" padding="md">
      <Stack gap="sm">
        <Group justify="space-between">
          <RequestStateBadge state={r.state} />
          {r.blocksIssue && <Badge color="red" variant="filled" leftSection={<IconAlertTriangle size={12} />}>{t.requests.blocksIssue}</Badge>}
        </Group>
        <TurnLine request={r} me={me} />
        <Divider />
        {r.neededBy && (
          <Field label={t.requests.neededBy}>
            <Text span c={r.overdue ? 'red' : undefined} fw={r.overdue ? 700 : undefined}>{day(r.neededBy)}</Text>
            {r.overdue && <Text span c="red" size="xs"> · {t.requests.overdue}</Text>}
          </Field>
        )}
        {r.promisedBy && <Field label={t.requests.promisedBy}>{day(r.promisedBy)}</Field>}
        <Field label={t.requests.direction}>{r.directionName ?? t.requests.directionCommon}</Field>
        <Field label={t.requests.assignee}>{r.assigneeName ?? '—'}</Field>
        <Field label={t.requests.author}>{r.authorName}</Field>
        {others.length > 0 && <Field label={t.requests.joined}>{others.map((m) => m.name).join(', ')}</Field>}
        {r.project && <Field label={t.requests.project}>{r.project}</Field>}
        {r.revitYear && <Field label={t.requests.revit}>{r.revitYear}</Field>}
        {r.section && <Field label={t.families.section}>{r.section}</Field>}
        <Field label={t.requests.waiting}>{r.waitingDays} {t.requests.days}</Field>
        {r.cardId && r.cardName && (
          <>
            <Divider label={t.requests.result} labelPosition="left" />
            <Button variant="light" color="teal" leftSection={<IconCheck size={16} />} onClick={() => onOpenCard(r.cardId!)} justify="flex-start">
              {r.cardName}
            </Button>
          </>
        )}
        {actions}
      </Stack>
    </Card>
  );
}

/** Плашки над описанием: приёмка, вопрос автору, итог закрытой заявки. */
export function Banners({ view, me, onOpenCard }: { view: RequestView; me: string | undefined; onOpenCard: (id: string) => void }) {
  const r = view.request;
  const member = r.isMember && r.assigneeId !== me;
  if (r.state === 'Review' && member) return <ReviewBanner view={view} onOpenCard={onOpenCard} />;
  if (r.state === 'Review')
    return <Alert color="violet" icon={<IconClock size={18} />} title={t.requestState.Review}>{r.cardName}</Alert>;
  if (r.state === 'NeedInfo' && member)
    return <Alert color="yellow" icon={<IconUser size={18} />} title={t.requests.needInfoTitle}>{t.requests.needInfoHint}</Alert>;
  if (r.state === 'Done' || r.state === 'Exists')
    return <Alert color="teal" icon={<IconCheck size={18} />} title={r.state === 'Exists' ? t.requests.existsTitle : t.requests.doneTitle}>{r.cardName}</Alert>;
  if (r.state === 'Rejected') return <Alert color="gray" title={t.requests.rejectedTitle}>{view.reason}</Alert>;
  if (r.state === 'Withdrawn') return <Alert color="gray" title={t.requests.withdrawnTitle} />;
  return null;
}

function ReviewBanner({ view, onOpenCard }: { view: RequestView; onOpenCard: (id: string) => void }) {
  const r = view.request;
  const [declining, setDeclining] = useState(false);
  const [reason, setReason] = useState('');
  const options = (success: string) => ({ invalidate: [requestsKey, familiesKey], success });
  const accept = useApiMutation('POST', () => `/api/families/requests/${r.id}/accept`, { ...options(t.requests.acceptedDone), json: () => undefined });
  const decline = useApiMutation<string>('POST', () => `/api/families/requests/${r.id}/decline`, {
    ...options(t.requests.declinedDone), json: (text) => ({ text, fileIds: [] }),
  });
  return (
    <Paper withBorder radius="md" p="md" style={{ borderColor: 'var(--mantine-color-violet-5)', borderWidth: 2 }}>
      <Stack gap="sm">
        <Text fw={700} size="lg">{r.resolution === 'Existed' ? t.requests.reviewExistsTitle : t.requests.reviewTitle}</Text>
        {r.cardId && r.cardName && (
          <Button variant="light" w="fit-content" onClick={() => onOpenCard(r.cardId!)}>{t.requests.openFamily}: {r.cardName}</Button>
        )}
        {view.autoAcceptAt && <Text size="sm" c="dimmed">{t.requests.reviewHint} {dateTime(view.autoAcceptAt)}.</Text>}
        <Group>
          <Button color="teal" leftSection={<IconCheck size={16} />} loading={accept.isPending} onClick={() => accept.mutate()}>{t.requests.accept}</Button>
          <Button variant="default" onClick={() => setDeclining(true)}>{t.requests.decline}</Button>
        </Group>
      </Stack>
      <Modal opened={declining} onClose={() => setDeclining(false)} title={t.requests.declineTitle}>
        <Stack>
          <Text size="sm" c="dimmed">{t.requests.declineHint}</Text>
          <Textarea autosize minRows={3} value={reason} onChange={(e) => setReason(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setDeclining(false)}>{t.common.cancel}</Button>
            <Button disabled={reason.trim() === ''} loading={decline.isPending}
              onClick={() => decline.mutate(reason.trim(), { onSuccess: () => setDeclining(false) })}>{t.requests.send}</Button>
          </Group>
        </Stack>
      </Modal>
    </Paper>
  );
}

/** Лента: события строкой, сообщения карточкой с вложениями. */
export function Timeline({ view, onOpenFile }: { view: RequestView; onOpenFile: (fileId: string) => void }) {
  const r = view.request;
  return (
    <Stack gap="xs">
      {view.messages.map((m) => m.isEvent ? (
        <Text key={m.id} size="xs" c="dimmed" pl="sm">{dateTime(m.at)} · {m.authorName ? `${m.authorName}: ` : ''}{m.text}</Text>
      ) : (
        <Paper key={m.id} withBorder p="sm" radius="md" data-message={m.id}
          bg={m.authorId === r.authorId ? undefined : 'var(--mantine-color-default-hover)'}>
          <Group justify="space-between" mb={4}>
            <Text size="sm" fw={600}>{m.authorName}</Text>
            <Text size="xs" c="dimmed">{dateTime(m.at)}</Text>
          </Group>
          {m.text && <Linkify text={m.text} />}
          {m.fileIds.length > 0 && (
            <AttachmentGrid cols={{ base: 2, sm: 3 }} items={gridItems(r.id, view.files.filter((f) => m.fileIds.includes(f.id)))}
              onOpen={(index) => onOpenFile(m.fileIds[index])} />
          )}
        </Paper>
      ))}
    </Stack>
  );
}
