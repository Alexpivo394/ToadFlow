import { useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import { Alert, Anchor, Badge, Button, FileButton, Group, Modal, Paper, Select, Stack, Text, Textarea, TextInput, Title } from '@mantine/core';
import { IconPaperclip } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch, downloadFile } from '../../api/client';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { QueryError } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import { familiesKey, openRequest, requestsKey, type FamilyPage, type RequestView } from './api';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { RequestStateBadge } from './RequestsPage';

const dateTime = (iso: string) => new Date(iso).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' });
const size = (bytes: number) => (bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} МБ` : `${Math.max(1, Math.round(bytes / 1024))} КБ`);

/** Заявка: что нужно, переписка, вложения; участнику — присоединиться и писать, координатору — вести её. Открытие отмечает «видел». */
export function RequestPage() {
  const { id = '' } = useParams();
  const auth = useAuth();
  const queryClient = useQueryClient();
  const request = useApi<RequestView>([requestsKey, 'one', id], `/api/families/requests/${id}`);
  const view = request.data;
  const [card, setCard] = useState<string | null>(null);
  const staff = auth.isStaff && auth.can('Operator');

  const seen = useRef<string | null>(null);
  useEffect(() => {
    if (!view?.request.unread || seen.current === view.request.updatedAt) return;
    seen.current = view.request.updatedAt;
    void apiFetch(`/api/families/requests/${id}/seen`, { method: 'POST' }).then(() => queryClient.invalidateQueries({ queryKey: [requestsKey] }));
  }, [id, view?.request.unread, view?.request.updatedAt, queryClient]);

  if (!view) return <QueryError query={request} />;
  const r = view.request;
  const isOpen = openRequest(r.state);
  const canWrite = staff || r.isMember;

  return (
    <Stack gap="md" maw={900}>
      <Anchor component={Link} to="/families/requests" size="sm">← {t.requests.title}</Anchor>
      <Group justify="space-between" align="flex-start">
        <Stack gap={4}>
          <Title order={3}>№ {r.number} · {r.title}</Title>
          <Group gap={6}>
            <RequestStateBadge state={r.state} />
            {r.urgency === 'Urgent' && <Badge color="red" variant="filled">{t.requests.urgent}</Badge>}
            {r.section && <Badge variant="outline">{r.section}</Badge>}
            {r.project && <Text size="sm" c="dimmed">{t.requests.project}: {r.project}</Text>}
          </Group>
          <Text size="sm" c="dimmed">
            {t.requests.author}: {r.authorName} · {dateTime(r.createdAt)}
            {r.assigneeName && ` · ${t.requests.assignee}: ${r.assigneeName}`}
            {view.members.length > 1 && ` · ${t.requests.joined}: ${view.members.filter((m) => m.userId !== r.authorId).map((m) => m.name).join(', ')}`}
          </Text>
        </Stack>
        <Group gap={6}>
          {auth.canWork && !r.isMember && isOpen && <JoinButton id={id} join />}
          {auth.canWork && r.isMember && r.authorId !== auth.user?.id && <JoinButton id={id} join={false} />}
        </Group>
      </Group>

      {r.cardId && r.cardName && (
        <Alert color="teal" title={r.state === 'Exists' ? t.requests.existsTitle : t.requests.doneTitle}>
          <Anchor component="button" onClick={() => setCard(r.cardId)}>{r.cardName}</Anchor>
        </Alert>
      )}
      {r.state === 'Rejected' && view.reason && <Alert color="gray" title={t.requests.rejectedTitle}>{view.reason}</Alert>}
      {view.text && <Paper withBorder p="sm"><Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{view.text}</Text></Paper>}

      {staff && <CoordinatorActions view={view} />}

      <Stack gap={4}>
        <Title order={5}>{t.requests.files}</Title>
        {view.files.length === 0 && <Text size="sm" c="dimmed">{t.requests.noFiles}</Text>}
        {view.files.map((file) => (
          <Group key={file.id} gap={6}>
            <Anchor size="sm" component="button" onClick={() => void downloadFile(`/api/families/requests/${id}/files/${file.id}`, file.fileName)}>{file.fileName}</Anchor>
            <Text size="xs" c="dimmed">{size(file.size)} · {file.uploadedByName} · {dateTime(file.at)}</Text>
          </Group>
        ))}
        {canWrite && auth.canWork && <UploadButton id={id} />}
      </Stack>

      <Stack gap="xs">
        <Title order={5}>{t.requests.conversation}</Title>
        {view.messages.map((m) => m.isEvent ? (
          <Text key={m.id} size="xs" c="dimmed">{dateTime(m.at)} · {m.authorName ? `${m.authorName}: ` : ''}{m.text}</Text>
        ) : (
          <Paper key={m.id} withBorder p="xs">
            <Text size="xs" c="dimmed">{m.authorName} · {dateTime(m.at)}</Text>
            <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{m.text}</Text>
          </Paper>
        ))}
        {canWrite && auth.canWork ? <MessageBox id={id} /> : auth.canWork && <Text size="sm" c="dimmed">{t.requests.joinToWrite}</Text>}
      </Stack>
      <FamilyCardDrawer cardId={card} unreviewed={false} onClose={() => setCard(null)} />
    </Stack>
  );
}

function JoinButton({ id, join }: { id: string; join: boolean }) {
  const action = useApiMutation('POST', () => `/api/families/requests/${id}/${join ? 'join' : 'leave'}`,
    { invalidate: [requestsKey], success: join ? t.requests.joinedDone : t.requests.leftDone, json: () => undefined });
  return <Button variant={join ? 'filled' : 'default'} size="xs" loading={action.isPending} onClick={() => action.mutate()}>{join ? t.requests.join : t.requests.leave}</Button>;
}

function MessageBox({ id }: { id: string }) {
  const [text, setText] = useState('');
  const send = useApiMutation<string>('POST', () => `/api/families/requests/${id}/messages`, { invalidate: [requestsKey], json: (value) => ({ text: value }) });
  return (
    <Stack gap={4}>
      <Textarea placeholder={t.requests.messagePlaceholder} autosize minRows={2} value={text} onChange={(e) => setText(e.currentTarget.value)} aria-label={t.requests.message} />
      <Group>
        <Button size="xs" disabled={text.trim() === ''} loading={send.isPending} onClick={() => send.mutate(text.trim(), { onSuccess: () => setText('') })}>
          {t.requests.send}
        </Button>
      </Group>
    </Stack>
  );
}

function UploadButton({ id }: { id: string }) {
  const queryClient = useQueryClient();
  const upload = useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();
      form.append('file', file);
      return apiFetch(`/api/families/requests/${id}/files`, { method: 'POST', body: form });
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [requestsKey] }),
    onError: (error: Error) => notifications.show({ color: 'red', title: 'Не получилось', message: error.message }),
  });
  return (
    <FileButton onChange={(file) => file && upload.mutate(file)}>
      {(props) => <Button {...props} size="xs" variant="default" leftSection={<IconPaperclip size={14} />} loading={upload.isPending} w="fit-content">{t.requests.attach}</Button>}
    </FileButton>
  );
}

/** Координатор: взять, передать, «готова» или «уже есть» со ссылкой на семейство, отклонить с причиной, вернуть в работу. */
function CoordinatorActions({ view }: { view: RequestView }) {
  const r = view.request;
  const id = r.id;
  const auth = useAuth();
  const path = (action: string) => () => `/api/families/requests/${id}/${action}`;
  const options = (success: string) => ({ invalidate: [requestsKey, familiesKey], success });
  const take = useApiMutation('POST', path('take'), options(t.requests.taken));
  const reopen = useApiMutation('POST', path('reopen'), options(t.requests.reopened));
  const assign = useApiMutation<{ userId: string }>('POST', path('assign'), options(t.requests.assigned));
  const done = useApiMutation<{ cardId: string }>('POST', path('done'), options(t.requests.closed));
  const exists = useApiMutation<{ cardId: string }>('POST', path('exists'), options(t.requests.closed));
  const reject = useApiMutation<{ reason: string }>('POST', path('reject'), options(t.requests.closed));
  const assignees = useApi<{ id: string; name: string }[]>([requestsKey, 'assignees'], '/api/families/requests/assignees');
  const [picking, setPicking] = useState<'done' | 'exists' | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [reason, setReason] = useState('');

  if (!openRequest(r.state))
    return <Group><Button size="xs" variant="default" loading={reopen.isPending} onClick={() => reopen.mutate()}>{t.requests.reopen}</Button></Group>;

  return (
    <Group gap={6}>
      {r.assigneeId !== auth.user?.id && (
        <Button size="xs" loading={take.isPending} onClick={() => take.mutate()}>{t.requests.take}</Button>
      )}
      <Select size="xs" w={220} placeholder={t.requests.passTo} data={(assignees.data ?? []).map((a) => ({ value: a.id, label: a.name }))} value={null}
        onChange={(value) => value && assign.mutate({ userId: value })} aria-label={t.requests.passTo} />
      <Button size="xs" color="teal" variant="light" onClick={() => setPicking('done')}>{t.requests.markDone}</Button>
      <Button size="xs" color="teal" variant="light" onClick={() => setPicking('exists')}>{t.requests.markExists}</Button>
      <Button size="xs" color="red" variant="light" onClick={() => setRejecting(true)}>{t.requests.reject}</Button>
      <CardPicker opened={picking !== null} title={picking === 'exists' ? t.requests.markExists : t.requests.markDone} onClose={() => setPicking(null)}
        onPick={(cardId) => (picking === 'exists' ? exists : done).mutate({ cardId }, { onSuccess: () => setPicking(null) })} />
      <Modal opened={rejecting} onClose={() => setRejecting(false)} title={t.requests.reject}>
        <Stack>
          <Textarea label={t.families.reason} autosize minRows={2} value={reason} onChange={(e) => setReason(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRejecting(false)}>{t.common.cancel}</Button>
            <Button color="red" disabled={reason.trim() === ''} loading={reject.isPending}
              onClick={() => reject.mutate({ reason: reason.trim() }, { onSuccess: () => setRejecting(false) })}>{t.requests.reject}</Button>
          </Group>
        </Stack>
      </Modal>
    </Group>
  );
}

/** Выбор семейства каталога поиском — для «готова» и «уже есть». */
function CardPicker({ opened, title, onClose, onPick }: { opened: boolean; title: string; onClose: () => void; onPick: (cardId: string) => void }) {
  const [q, setQ] = useState('');
  const found = useApi<FamilyPage>([familiesKey, 'pick', q], opened && q.trim().length >= 2 ? `/api/families/search?q=${encodeURIComponent(q.trim())}&pageSize=10` : null);
  return (
    <Modal opened={opened} onClose={onClose} title={title}>
      <Stack gap="xs">
        <TextInput placeholder={t.families.searchPlaceholder} value={q} onChange={(e) => setQ(e.currentTarget.value)} data-autofocus aria-label={t.requests.findFamily} />
        {(found.data?.items ?? []).map((hit) => (
          <Button key={hit.id} variant="default" justify="space-between" onClick={() => onPick(hit.id)}
            rightSection={<Text size="xs" c="dimmed">{hit.section}</Text>}>{hit.name}</Button>
        ))}
      </Stack>
    </Modal>
  );
}
