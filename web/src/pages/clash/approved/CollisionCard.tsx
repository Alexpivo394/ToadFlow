import { useState } from 'react';
import { Alert, Button, Group, ScrollArea, SegmentedControl, Stack, Text, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { CollisionAssignee, CollisionCard as Card, CollisionStatus, FeedEntry } from '../../../api/types';
import { askConfirm } from '../../../components/ui';
import { formatDateTime } from '../../../lib/format';
import { assigneeLabel, statusLabel, StatusBadge } from './StatusBadge';

function FeedLine({ entry }: { entry: FeedEntry }) {
  const status = entry.kind === 'Status' && entry.fromStatus && entry.toStatus
    ? `${statusLabel[entry.fromStatus].label} → ${statusLabel[entry.toStatus].label}`
    : null;
  return (
    <Stack gap={0}>
      <Text size="xs" c="dimmed">{formatDateTime(entry.at)} · {entry.userName ?? 'система'}</Text>
      {status && <Text size="sm" fw={500}>{status}</Text>}
      {entry.text && <Text size="sm" c={entry.kind === 'Depth' ? 'dimmed' : undefined}>{entry.text}</Text>}
    </Stack>
  );
}

/**
 * Карточка коллизии под 3D: статус и ручные переходы («фиктивная» — с примечанием), «кто правит», примечание, предложение
 * объединить с прежней коллизией и лента, новые сверху. Наблюдатель только читает.
 */
export function CollisionCard({ collisionId, canEdit, canMerge }: { collisionId: string; canEdit: boolean; canMerge: boolean }) {
  const readOnly = !canEdit;
  const queryClient = useQueryClient();
  const card = useApi<Card>(['clash-collisions', 'card', collisionId], `/api/clash/collisions/${collisionId}`);
  const [faking, setFaking] = useState(false);
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const c = card.data?.collision;
  if (!card.data || !c) return null;

  async function post(path: string, body: unknown, success: string) {
    setBusy(true);
    try {
      await apiFetch(`/api/clash/collisions/${collisionId}/${path}`, { method: 'POST', json: body });
      notifications.show({ color: 'green', message: success });
      await queryClient.invalidateQueries({ queryKey: ['clash-collisions'] });
      return true;
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
      return false;
    } finally {
      setBusy(false);
    }
  }

  const setStatus = (status: CollisionStatus, text?: string) => post('status', { status, note: text ?? null }, `Статус: ${statusLabel[status].label}`);
  const open = c.status === 'New' || c.status === 'Active' || c.status === 'Returned';

  return (
    <Stack gap="xs" h="100%">
      <Group gap="xs">
        <Text fw={700}>№ {c.number}</Text>
        <StatusBadge status={c.status} />
        {c.status === 'Fake' && !c.foundNow && <Text size="xs" c="dimmed">сейчас не пересекается</Text>}
        <Text size="xs" c="dimmed">найдена версией {c.firstVersion}{c.lastFoundVersion !== c.firstVersion ? `, последняя — ${c.lastFoundVersion}` : ''}</Text>
      </Group>
      {c.suggestedPredecessorId && c.suggestedPredecessorNumber != null && (
        <Alert color="blue" p="xs">
          <Group justify="space-between" wrap="nowrap">
            <Text size="sm">Возможно, это коллизия №{c.suggestedPredecessorNumber}: элемент пересоздан с новым UniqueId.</Text>
            {canMerge && (
              <Button size="xs" variant="light" loading={busy}
                onClick={() => askConfirm(`Объединить с коллизией №${c.suggestedPredecessorNumber}? Останутся её номер, статус и лента.`,
                  () => void post('merge', { predecessorId: c.suggestedPredecessorId }, `Объединена с №${c.suggestedPredecessorNumber}`), 'blue')}>
                Объединить
              </Button>
            )}
          </Group>
        </Alert>
      )}
      {!readOnly && (
        <Group gap="xs">
          {open && <Button size="xs" variant="light" color="teal" loading={busy} onClick={() => setStatus('FixedPending')}>Исправлена (ожидает подтверждения)</Button>}
          {c.status !== 'Fake' && <Button size="xs" variant="light" color="gray" onClick={() => setFaking((f) => !f)}>Фиктивная…</Button>}
          {c.status === 'Fake' && <Button size="xs" variant="light" loading={busy} onClick={() => setStatus('Active')}>Снять «фиктивная»</Button>}
          <SegmentedControl size="xs" value={c.assignee}
            onChange={(v) => void post('assignee', { assignee: v }, `Кто правит: ${assigneeLabel[v as CollisionAssignee]}`)}
            data={(['Both', 'SideA', 'SideB'] as CollisionAssignee[]).map((a) => ({ value: a, label: assigneeLabel[a] }))} />
        </Group>
      )}
      {readOnly && <Text size="xs" c="dimmed">Кто правит: {assigneeLabel[c.assignee]}</Text>}
      {faking && !readOnly && (
        <Group align="flex-end" gap="xs">
          <Textarea label="Почему фиктивная" size="xs" autosize minRows={1} style={{ flex: 1 }} value={reason} onChange={(e) => setReason(e.currentTarget.value)} />
          <Button size="xs" color="gray" disabled={!reason.trim()} loading={busy}
            onClick={async () => { if (await setStatus('Fake', reason.trim())) { setFaking(false); setReason(''); } }}>
            Отметить фиктивной
          </Button>
        </Group>
      )}
      {!readOnly && (
        <Group align="flex-end" gap="xs">
          <Textarea label="Примечание" size="xs" autosize minRows={1} style={{ flex: 1 }} value={note} onChange={(e) => setNote(e.currentTarget.value)} />
          <Button size="xs" variant="default" disabled={!note.trim()} loading={busy}
            onClick={async () => { if (await post('notes', { text: note.trim() }, 'Примечание добавлено')) setNote(''); }}>
            Добавить
          </Button>
        </Group>
      )}
      <ScrollArea style={{ flex: 1 }} type="auto">
        <Stack gap="xs">
          {card.data.feed.map((e, i) => <FeedLine key={`${e.at}-${e.kind}-${i}`} entry={e} />)}
        </Stack>
      </ScrollArea>
    </Stack>
  );
}
