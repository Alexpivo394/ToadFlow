import { useState } from 'react';
import { Button, Checkbox, Group, Modal, Select, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { DateInput } from '@mantine/dates';
import { IconMessageQuestion, IconSend } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import { useApi, useApiMutation } from '../../../api/queries';
import { useAuth } from '../../../auth/AuthContext';
import { t } from '../../../i18n/ru';
import { familiesKey, openRequest, requestsKey, type Direction, type FamilyPage, type RequestEdit, type RequestView } from '../api';
import { AttachmentGrid } from '../attachments/AttachmentGrid';
import { DropArea } from '../attachments/DropArea';
import { useUploads } from '../attachments/useUploads';

const path = (id: string, action: string) => () => `/api/families/requests/${id}/${action}`;
const options = (success: string) => ({ invalidate: [requestsKey, familiesKey], success });

/** Сообщение с вложениями; координатору — быстрые ответы и «Спросить автора» (ход переходит к автору). */
export function Composer({ id, staff }: { id: string; staff: boolean }) {
  const [text, setText] = useState('');
  const uploads = useUploads();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const send = async (action: 'messages' | 'ask') => {
    setBusy(true);
    try {
      await apiFetch(`/api/families/requests/${id}/${action}`, { method: 'POST', json: { text: text.trim(), fileIds: uploads.ids } });
      setText('');
      uploads.clear();
      void queryClient.invalidateQueries({ queryKey: [requestsKey] });
    } finally {
      setBusy(false);
    }
  };
  const empty = text.trim() === '' && uploads.ids.length === 0;
  return (
    <Stack gap="xs">
      {staff && (
        <Group gap={6}>
          {t.requests.quickReplies.map((reply) => (
            <Button key={reply} size="compact-xs" variant="light" color="gray" onClick={() => setText(text ? `${text}\n${reply}` : reply)}>{reply}</Button>
          ))}
        </Group>
      )}
      <Textarea placeholder={t.requests.messagePlaceholder} autosize minRows={2} value={text} onChange={(e) => setText(e.currentTarget.value)}
        aria-label={t.requests.message} />
      <AttachmentGrid cols={{ base: 3, sm: 5 }} items={uploads.items} onRemove={uploads.remove} />
      <Group justify="space-between">
        <DropArea compact onFiles={uploads.add} />
        <Group gap="xs">
          {staff && (
            <Button size="xs" variant="light" color="yellow" leftSection={<IconMessageQuestion size={14} />} disabled={text.trim() === '' || uploads.busy}
              loading={busy} onClick={() => void send('ask')}>{t.requests.ask}</Button>
          )}
          <Button size="xs" leftSection={<IconSend size={14} />} disabled={empty || uploads.busy} loading={busy} onClick={() => void send('messages')}>
            {t.requests.send}
          </Button>
        </Group>
      </Group>
    </Stack>
  );
}

/** Присоединиться, выйти, изменить, отозвать — участнику и автору. */
export function MemberActions({ view }: { view: RequestView }) {
  const auth = useAuth();
  const r = view.request;
  const [withdrawing, setWithdrawing] = useState(false);
  const [editing, setEditing] = useState(false);
  const join = useApiMutation('POST', path(r.id, 'join'), { ...options(t.requests.joinedDone), json: () => undefined });
  const leave = useApiMutation('POST', path(r.id, 'leave'), { ...options(t.requests.leftDone), json: () => undefined });
  const withdraw = useApiMutation('POST', path(r.id, 'withdraw'), { ...options(t.requests.withdrawn), json: () => undefined });
  const author = r.authorId === auth.user?.id;
  const open = openRequest(r.state);
  if (!auth.canWork) return null;
  return (
    <Stack gap={6}>
      {!r.isMember && open && <Button loading={join.isPending} onClick={() => join.mutate()}>{t.requests.join}</Button>}
      {r.isMember && !author && <Button variant="default" loading={leave.isPending} onClick={() => leave.mutate()}>{t.requests.leave}</Button>}
      {author && open && (
        <Group grow gap={6}>
          <Button variant="default" onClick={() => setEditing(true)}>{t.requests.edit}</Button>
          <Button variant="default" color="red" onClick={() => setWithdrawing(true)}>{t.requests.withdraw}</Button>
        </Group>
      )}
      <Modal opened={withdrawing} onClose={() => setWithdrawing(false)} title={t.requests.withdraw}>
        <Stack>
          <Text size="sm">{t.requests.withdrawConfirm}</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setWithdrawing(false)}>{t.common.cancel}</Button>
            <Button color="red" loading={withdraw.isPending} onClick={() => withdraw.mutate(undefined, { onSuccess: () => setWithdrawing(false) })}>
              {t.requests.withdraw}
            </Button>
          </Group>
        </Stack>
      </Modal>
      <EditModal view={view} opened={editing} onClose={() => setEditing(false)} />
    </Stack>
  );
}

export function EditModal({ view, opened, onClose }: { view: RequestView; opened: boolean; onClose: () => void }) {
  const r = view.request;
  const [form, setForm] = useState<RequestEdit>({
    title: r.title, text: view.text, section: r.section, project: r.project, neededBy: r.neededBy, blocksIssue: r.blocksIssue, revitYear: r.revitYear,
  });
  const save = useApiMutation<RequestEdit>('PUT', () => `/api/families/requests/${r.id}`, options(t.requests.saved));
  return (
    <Modal opened={opened} onClose={onClose} title={t.requests.editTitle} size="lg">
      <Stack>
        <TextInput label={t.requests.what} value={form.title} maxLength={200} onChange={(e) => setForm({ ...form, title: e.currentTarget.value })} />
        <Textarea label={t.requests.details} autosize minRows={4} maxLength={8000} value={form.text} onChange={(e) => setForm({ ...form, text: e.currentTarget.value })} />
        <Group grow>
          <TextInput label={t.requests.project} value={form.project} onChange={(e) => setForm({ ...form, project: e.currentTarget.value })} />
          <DateInput label={t.requests.neededBy} clearable valueFormat="DD.MM.YYYY" value={form.neededBy}
            onChange={(value) => setForm({ ...form, neededBy: value ? String(value).slice(0, 10) : null })} />
        </Group>
        <Checkbox label={t.requests.blocksIssue} checked={form.blocksIssue} onChange={(e) => setForm({ ...form, blocksIssue: e.currentTarget.checked })} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t.common.cancel}</Button>
          <Button disabled={form.title.trim() === ''} loading={save.isPending} onClick={() => save.mutate(form, { onSuccess: onClose })}>{t.requests.save}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Координатор: взять, обещать срок, передать, сменить направление, «готово» / «уже есть», отклонить; закрытую — вернуть в работу. */
export function CoordinatorActions({ view }: { view: RequestView }) {
  const r = view.request;
  const auth = useAuth();
  const take = useApiMutation('POST', path(r.id, 'take'), { ...options(t.requests.taken), json: () => undefined });
  const reopen = useApiMutation('POST', path(r.id, 'reopen'), { ...options(t.requests.reopened), json: () => undefined });
  const assign = useApiMutation<{ userId: string }>('POST', path(r.id, 'assign'), options(t.requests.assigned));
  const move = useApiMutation<{ directionId: string | null }>('POST', path(r.id, 'direction'), options(t.requests.moved));
  const promise = useApiMutation<{ promisedBy: string | null }>('POST', path(r.id, 'promise'), options(t.requests.promised));
  const done = useApiMutation<{ cardId: string }>('POST', path(r.id, 'done'), options(t.requests.closed));
  const exists = useApiMutation<{ cardId: string }>('POST', path(r.id, 'exists'), options(t.requests.closed));
  const reject = useApiMutation<{ reason: string }>('POST', path(r.id, 'reject'), options(t.requests.rejected));
  const assignees = useApi<{ id: string; name: string }[]>([requestsKey, 'assignees'], '/api/families/requests/assignees');
  const directions = useApi<Direction[]>([requestsKey, 'directions'], '/api/families/directions');
  const [picking, setPicking] = useState<'done' | 'exists' | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [reason, setReason] = useState('');

  if (!openRequest(r.state))
    return <Button variant="default" loading={reopen.isPending} onClick={() => reopen.mutate()}>{t.requests.reopen}</Button>;

  return (
    <Stack gap={6}>
      {r.assigneeId !== auth.user?.id && <Button loading={take.isPending} onClick={() => take.mutate()}>{t.requests.take}</Button>}
      <DateInput size="xs" label={t.requests.promise} clearable valueFormat="DD.MM.YYYY" value={r.promisedBy}
        onChange={(value) => promise.mutate({ promisedBy: value ? String(value).slice(0, 10) : null })} />
      <Select size="xs" placeholder={t.requests.passTo} data={(assignees.data ?? []).map((a) => ({ value: a.id, label: a.name }))} value={null}
        onChange={(value) => value && assign.mutate({ userId: value })} aria-label={t.requests.passTo} />
      {(directions.data ?? []).length > 0 && (
        <Select size="xs" placeholder={t.requests.moveTo} aria-label={t.requests.moveTo} value={null}
          data={[...(directions.data ?? []).filter((d) => d.id !== r.directionId).map((d) => ({ value: d.id, label: d.name })),
            ...(r.directionId ? [{ value: '', label: t.requests.directionCommon }] : [])]}
          onChange={(value) => value !== null && move.mutate({ directionId: value || null })} />
      )}
      <Group grow gap={6}>
        <Button size="xs" color="teal" variant="light" onClick={() => setPicking('done')}>{t.requests.markDone}</Button>
        <Button size="xs" color="teal" variant="light" onClick={() => setPicking('exists')}>{t.requests.markExists}</Button>
      </Group>
      <Button size="xs" color="red" variant="subtle" onClick={() => setRejecting(true)}>{t.requests.reject}</Button>
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
    </Stack>
  );
}

/** Выбор семейства каталога поиском — для «готово» и «уже есть». */
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
