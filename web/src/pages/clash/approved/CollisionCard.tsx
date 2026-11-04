import { useEffect, useState } from 'react';
import { Alert, Anchor, Badge, Button, Center, Group, Loader, Popover, Radio, ScrollArea, Stack, Text, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import { QueryError } from '../../../components/QueryState';
import type { CollisionAssignee, CollisionCard as Card, CollisionPage, CollisionStatus, FeedEntry, UserCard } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { CollisionImage } from '../../../components/CollisionImage';
import { askConfirm } from '../../../components/ui';
import { copyText } from '../../../lib/clipboard';
import { formatDateTime } from '../../../lib/format';
import { assigneeText, statusLabel, StatusBadge } from './StatusBadge';

/** Страница списка с отметкой «ещё не открывал» у этой коллизии снята; другие данные (фасеты, группы) — как были. */
export function markSeen<T extends { items?: unknown[] } | null | undefined>(data: T, collisionId: string): T {
  if (!data || !Array.isArray(data.items)) return data;
  const isTarget = (item: unknown) => {
    const row = item as { collision?: { id?: string }; unseen?: boolean };
    return row.unseen === true && row.collision?.id === collisionId;
  };
  if (!data.items.some(isTarget)) return data;
  return { ...data, items: data.items.map((item) => (isTarget(item) ? { ...(item as object), unseen: false } : item)) };
}

/** Имя автора записи ленты: по нажатию — должность и контакты, чтобы связаться. */
function Author({ userId, name }: { userId: string; name: string }) {
  const [opened, setOpened] = useState(false);
  const card = useApi<UserCard>(['user-card', userId], opened ? `/api/users/${userId}/card` : null);
  return (
    <Popover opened={opened} onChange={setOpened} position="bottom-start" withArrow shadow="md">
      <Popover.Target>
        <Anchor component="button" size="xs" onClick={() => setOpened((o) => !o)}>{name}</Anchor>
      </Popover.Target>
      <Popover.Dropdown>
        {card.data ? (
          <Stack gap={2}>
            <Text size="sm" fw={600}>{card.data.displayName}</Text>
            {card.data.position && <Text size="xs">{card.data.position}</Text>}
            {card.data.disciplines.length > 0 && <Text size="xs" c="dimmed">Разделы: {card.data.disciplines.join(', ')}</Text>}
            {card.data.email && <Anchor size="xs" href={`mailto:${card.data.email}`}>{card.data.email}</Anchor>}
            {card.data.phone && <Anchor size="xs" href={`tel:${card.data.phone}`}>{card.data.phone}</Anchor>}
          </Stack>
        ) : <Text size="xs" c="dimmed">…</Text>}
      </Popover.Dropdown>
    </Popover>
  );
}

function FeedLine({ entry }: { entry: FeedEntry }) {
  const status = entry.kind === 'Status' && entry.fromStatus && entry.toStatus
    ? `${statusLabel[entry.fromStatus].label} → ${statusLabel[entry.toStatus].label}`
    : null;
  return (
    <Stack gap={0}>
      <Text size="xs" c="dimmed">
        {formatDateTime(entry.at)} · {entry.userId && entry.userName ? <Author userId={entry.userId} name={entry.userName} /> : (entry.userName ?? 'система')}
        {entry.bulkId && <Badge size="xs" variant="light" color="gray" ml={6}>массово</Badge>}
      </Text>
      {status && <Text size="sm" fw={500}>{status}</Text>}
      {entry.text && <Text size="sm" c={entry.kind === 'Depth' ? 'dimmed' : undefined}>{entry.text}</Text>}
    </Stack>
  );
}

/**
 * Карточка коллизии под 3D: статус и ручные переходы («фиктивная» — с примечанием), «кто правит», примечание, предложение
 * объединить с прежней коллизией и лента, новые сверху. «Беру» — «кто правит» становится моей стороной (оба элемента из
 * моих разделов — обе стороны); «Передать…» — выбрать сторону и, если нужно, примечание. Статус и «кто правит» проектировщик
 * меняет только в коллизиях своего раздела, примечание пишет в любой. Наблюдатель только читает.
 *
 * Открытие карточки отмечает коллизию просмотренной («новые для меня»). С `partId` и `partName` — «Показать в Revit» (номер и
 * ссылка в буфер, панель плагина открывает по Ctrl+V) и строка «элемент ещё в N коллизиях → показать».
 */
export function CollisionCard({ collisionId, canEdit, canMerge, image = true, partId, partName, onShowElement }: {
  collisionId: string;
  canEdit: boolean;
  canMerge: boolean;
  /** Картинка коллизии над лентой, если она нарисована. */
  image?: boolean;
  partId?: string;
  partName?: string;
  /** Показать все коллизии элемента: ключ `{модель}:{UniqueId}` и раздел его стороны. */
  onShowElement?: (element: string, discipline: string) => void;
}) {
  const readOnly = !canEdit;
  const { disciplines, isStaff } = useAuth();
  const [handing, setHanding] = useState(false);
  const [side, setSide] = useState<CollisionAssignee | null>(null);
  const [handNote, setHandNote] = useState('');
  const queryClient = useQueryClient();
  const card = useApi<Card>(['clash-collisions', 'card', collisionId], `/api/clash/collisions/${collisionId}`);
  const [faking, setFaking] = useState(false);
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const c = card.data?.collision;

  // Открыл — значит увидел: коллизия уходит из «новых для меня». Точка у строки снимается в загруженных списках на месте,
  // без перезапроса: строки не прыгают (в «Новых для меня» строка остаётся до следующей загрузки списка).
  useEffect(() => {
    apiFetch(`/api/clash/collisions/${collisionId}/seen`, { method: 'POST' })
      .then(() => {
        queryClient.setQueriesData<{ items?: unknown[] } | null>({ queryKey: ['clash-list'] }, (data) => markSeen(data, collisionId));
        return queryClient.invalidateQueries({ queryKey: ['clash-my'] });
      })
      .catch(() => {});
  }, [collisionId, queryClient]);

  // Элемент своей стороны (или A): в скольких ещё коллизиях он участвует.
  const sideB = c ? disciplines.includes(c.disciplineB) && !disciplines.includes(c.disciplineA) : false;
  const element = c ? (sideB ? `${c.modelB}:${c.uniqueIdB}` : `${c.modelA}:${c.uniqueIdA}`) : null;
  const others = useApi<CollisionPage>(['clash-collisions', 'element-count', partId, element],
    partId && element && onShowElement ? `/api/clash/parts/${partId}/approved/collisions?element=${encodeURIComponent(element)}&pageSize=1` : null);
  if (card.isError) return <QueryError query={card} title="Коллизия не открылась" />;
  if (!card.data || !c)
    return (
      <Center py="md">
        <Loader size="sm" />
      </Center>
    );
  const more = Math.max(0, (others.data?.total ?? 0) - 1);

  const showInRevit = async () => {
    const text = `ToadClash №${c.number} — ${partName}\n${window.location.origin}/clash/checks/${partId}?collision=${c.id}`;
    if (await copyText(text)) setCopied(true);
    else notifications.show({ color: 'red', message: 'Браузер не дал скопировать — скопируйте номер вручную' });
  };

  async function post(path: string, body: unknown, success: string) {
    setBusy(true);
    try {
      await apiFetch(`/api/clash/collisions/${collisionId}/${path}`, { method: 'POST', json: body });
      notifications.show({ color: 'green', message: success });
      await queryClient.invalidateQueries({ queryKey: ['clash-collisions'] });
      await queryClient.invalidateQueries({ queryKey: ['clash-my'] });
      await queryClient.invalidateQueries({ queryKey: ['clash-list'] });
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
  const mine = disciplines.includes(c.disciplineA) || disciplines.includes(c.disciplineB);
  // Статус и «кто правит» проектировщик меняет только в коллизиях своего раздела; примечание пишет в любой.
  const canChange = !readOnly && (isStaff || mine);

  return (
    <Stack gap="xs" h="100%">
      <Group gap="xs">
        <Text fw={700}>№ {c.number}</Text>
        <StatusBadge status={c.status} />
        {c.status === 'Fake' && !c.foundNow && <Text size="xs" c="dimmed">сейчас не пересекается</Text>}
        <Text size="xs" c="dimmed">найдена версией {c.firstVersion}{c.lastFoundVersion !== c.firstVersion ? `, последняя — ${c.lastFoundVersion}` : ''}</Text>
        {partId && partName && <Button size="compact-xs" variant="default" onClick={() => void showInRevit()}>Показать в Revit</Button>}
      </Group>
      {copied && <Text size="xs" c="teal">Номер скопирован. В Revit откройте панель «Коллизии» и нажмите Ctrl+V</Text>}
      {more > 0 && element && onShowElement && (
        <Anchor component="button" size="xs" ta="left"
          onClick={() => onShowElement(element, sideB ? c.disciplineB : c.disciplineA)}>
          ID {sideB ? c.elementIdB : c.elementIdA} ещё в {more.toLocaleString('ru-RU')} коллизиях → показать
        </Anchor>
      )}
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
      {canChange && (
        <Group gap="xs">
          {open && <Button size="xs" variant="light" color="teal" loading={busy} onClick={() => setStatus('FixedPending')}>Исправлена (ожидает подтверждения)</Button>}
          {c.status !== 'Fake' && <Button size="xs" variant="light" color="gray" onClick={() => setFaking((f) => !f)}>Фиктивная…</Button>}
          {c.status === 'Fake' && <Button size="xs" variant="light" loading={busy} onClick={() => setStatus('Active')}>Снять «фиктивная»</Button>}
        </Group>
      )}
      {!readOnly && !canChange && (
        <Text size="xs" c="dimmed">Статус и «кто правит» меняет проектировщик раздела этой коллизии или координатор</Text>
      )}
      <Group gap="xs">
        <Text size="xs" c="dimmed">Кто правит: {assigneeText(c.assignee, c)}</Text>
        {!readOnly && mine && <Button size="xs" variant="light" loading={busy} onClick={() => void post('take', undefined, 'Взято: правите вы')}>Беру</Button>}
        {canChange && <Button size="xs" variant="subtle" onClick={() => setHanding((h) => !h)}>Передать…</Button>}
      </Group>
      {handing && canChange && (
        <Group align="flex-end" gap="xs">
          <Radio.Group label="Кто правит" size="xs" value={side} onChange={(v) => setSide(v as CollisionAssignee)}>
            <Group gap="xs" mt={4}>
              {(['Both', 'SideA', 'SideB'] as CollisionAssignee[]).filter((a) => a !== c.assignee)
                .map((a) => <Radio key={a} value={a} label={assigneeText(a, c)} />)}
            </Group>
          </Radio.Group>
          <Textarea label="Примечание к передаче" size="xs" autosize minRows={1} style={{ flex: 1 }} value={handNote}
            onChange={(e) => setHandNote(e.currentTarget.value)} />
          <Button size="xs" disabled={!side} loading={busy}
            onClick={async () => {
              if (side && await post('assignee', { assignee: side, note: handNote.trim() || null }, `Кто правит: ${assigneeText(side, c)}`)) {
                setHanding(false);
                setSide(null);
                setHandNote('');
              }
            }}>
            Передать
          </Button>
        </Group>
      )}
      {faking && canChange && (
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
      {image && <CollisionImage collisionId={c.id} imageVersion={c.imageVersion} width={240} />}
      <ScrollArea style={{ flex: 1 }} type="auto">
        <Stack gap="xs">
          {card.data.feed.map((e, i) => <FeedLine key={`${e.at}-${e.kind}-${i}`} entry={e} />)}
        </Stack>
      </ScrollArea>
    </Stack>
  );
}
