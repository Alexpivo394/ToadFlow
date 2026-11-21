import { useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import { Anchor, Badge, Grid, Group, Paper, Stack, Text, Title } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { QueryError } from '../../components/QueryState';
import { t } from '../../i18n/ru';
import { notificationsKey } from '../../notifications/api';
import { openRequest, requestFilePath, requestsKey, type RequestView } from './api';
import { AttachmentGrid } from './attachments/AttachmentGrid';
import { FileViewer, type ViewerFile } from './attachments/FileViewer';
import { Linkify } from './attachments/Linkify';
import { FamilyCardDrawer } from './FamilyCardDrawer';
import { Composer, CoordinatorActions, MemberActions } from './request/RequestActions';
import { Banners, dateTime, gridItems, StatusCard, Timeline } from './request/RequestParts';

/**
 * Заявка: слева — что нужно, галерея вложений, лента и композер; справа — состояние, чей ход, сроки, люди и действия. Открытие отмечает
 * «видел» и читает уведомления по этой заявке.
 */
export function RequestPage() {
  const { id = '' } = useParams();
  const auth = useAuth();
  const queryClient = useQueryClient();
  const request = useApi<RequestView>([requestsKey, 'one', id], `/api/families/requests/${id}`);
  const view = request.data;
  const [card, setCard] = useState<string | null>(null);
  const [viewing, setViewing] = useState<number | null>(null);
  const staff = auth.isStaff && auth.can('Operator');

  const seen = useRef<string | null>(null);
  useEffect(() => {
    if (!view?.request.unread || seen.current === view.request.updatedAt) return;
    seen.current = view.request.updatedAt;
    void apiFetch(`/api/families/requests/${id}/seen`, { method: 'POST' }).then(() => queryClient.invalidateQueries({ queryKey: [requestsKey] }));
  }, [id, view?.request.unread, view?.request.updatedAt, queryClient]);
  useEffect(() => {
    void apiFetch('/api/notifications/read-link', { method: 'POST', json: { link: `/families/requests/${id}` } })
      .then(() => queryClient.invalidateQueries({ queryKey: [notificationsKey] }), () => undefined);
  }, [id, view?.request.updatedAt, queryClient]);

  if (!view) return <QueryError query={request} />;
  const r = view.request;
  const canWrite = staff || r.isMember;
  const files: ViewerFile[] = view.files.map((f) => ({
    key: f.id, name: f.fileName, kind: gridItems(id, [f])[0].kind, path: requestFilePath(id, f.id, true), download: requestFilePath(id, f.id),
  }));
  const openFile = (fileId: string) => setViewing(view.files.findIndex((f) => f.id === fileId));

  return (
    <Stack gap="md" maw={1280}>
      <Anchor component={Link} to="/families/requests" size="sm">← {t.requests.title}</Anchor>
      <Grid gap="xl">
        <Grid.Col span={{ base: 12, md: 8 }}>
          <Stack gap="md">
            <Stack gap={4}>
              <Group gap="xs">
                <Text c="dimmed" fw={600}>№ {r.number}</Text>
                {r.kind === 'Fix' && <Badge color="orange" variant="light">{t.requests.fixOf}{r.targetCardName ? ` «${r.targetCardName}»` : ''}</Badge>}
              </Group>
              <Title order={2}>{r.title}</Title>
              <Text size="sm" c="dimmed">{r.authorName} · {dateTime(r.createdAt)}</Text>
            </Stack>
            <Banners view={view} me={auth.user?.id} onOpenCard={setCard} />
            {view.text && <Paper withBorder p="md" radius="md"><Linkify text={view.text} /></Paper>}
            {view.files.length > 0 && (
              <Stack gap={6}>
                <Text fw={600}>{t.requests.files}</Text>
                <AttachmentGrid items={gridItems(id, view.files)} onOpen={setViewing} />
              </Stack>
            )}
            <Stack gap="xs">
              <Text fw={600}>{t.requests.conversation}</Text>
              <Timeline view={view} onOpenFile={openFile} />
              {canWrite && auth.canWork && openRequest(r.state) && <Composer id={id} staff={staff} />}
              {!canWrite && auth.canWork && <Text size="sm" c="dimmed">{t.requests.joinToWrite}</Text>}
            </Stack>
          </Stack>
        </Grid.Col>
        <Grid.Col span={{ base: 12, md: 4 }}>
          <Stack pos="sticky" top={16}>
            <StatusCard view={view} me={auth.user?.id} onOpenCard={setCard} actions={
              <Stack gap="xs" mt="xs">
                <MemberActions view={view} />
                {staff && <CoordinatorActions view={view} />}
              </Stack>
            } />
          </Stack>
        </Grid.Col>
      </Grid>
      <FileViewer files={files} index={viewing} onIndex={setViewing} onClose={() => setViewing(null)} />
      <FamilyCardDrawer cardId={card} unreviewed={false} onClose={() => setCard(null)} />
    </Stack>
  );
}
