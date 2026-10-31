import { useCallback, useEffect, useState } from 'react';
import { Link as RouterLink, useParams, useSearchParams } from 'react-router';
import {
  Alert, Anchor, Breadcrumbs, Button, Group, Paper, Pagination, SegmentedControl, Select, SimpleGrid, Spoiler, Stack, Table, Text, TextInput, Title,
  UnstyledButton,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { apiFetch, downloadFile } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { CollisionStatus, ReportPage as Page, ReportView } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { Panel } from '../../../components/ui';
import { copyText } from '../../../lib/clipboard';
import { formatDuration } from '../../../lib/format';
import { statusLabel } from '../approved/StatusBadge';
import { ReportCollisionDrawer } from './ReportCollisionDrawer';
import { ReportRows } from './ReportRows';

const PageSize = 50;
const n = (value: number) => value.toLocaleString('ru-RU');

/** Дата и время целиком: «31.10.2026 12:05». */
export function formatFullDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  const pad = (v: number) => String(v).padStart(2, '0');
  return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Фильтры отчёта — в строке адреса: ссылку можно переслать. */
const filterKeys = ['discipline', 'pair', 'status', 'assignee', 'level', 'model', 'q'] as const;

function Tile({ label, value, color }: { label: string; value: number; color?: string }) {
  return (
    <Paper withBorder p="xs" radius="md">
      <Text fw={700} size="xl" c={color}>{n(value)}</Text>
      <Text size="xs" c="dimmed">{label}</Text>
    </Paper>
  );
}

/**
 * Отчёт утверждённого прогона: шапка, плитки сводки, пары разделов, изменения прогона или открытые сейчас с картинками и
 * фильтрами, выгрузки Excel и HTML. Координатор копирует сообщение для чата. Статусы в строках — текущие; у старого прогона —
 * плашка со ссылкой на более новый.
 */
export function ReportPage() {
  const { runId } = useParams();
  const { can, canWork } = useAuth();
  const [params, setParams] = useSearchParams();
  const [copying, setCopying] = useState(false);
  const [exporting, setExporting] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 2000);
    return () => clearTimeout(timer);
  }, [copied]);
  const report = useApi<ReportView>(['clash-collisions', 'report', runId], `/api/clash/reports/${runId}`);
  const scope = params.get('scope') === 'open' ? 'open' : 'changes';
  const page = Math.max(1, Number(params.get('page') ?? 1) || 1);
  const collisionId = params.get('collision');

  const update = useCallback((patch: Record<string, string | null>, resetPage = true) =>
    setParams((p) => {
      const next = new URLSearchParams(p);
      for (const [k, v] of Object.entries(patch)) if (v == null || v === '') next.delete(k); else next.set(k, v);
      if (resetPage) next.delete('page');
      return next;
    }, { replace: true }), [setParams]);

  const filters = new URLSearchParams({ scope });
  for (const key of filterKeys) {
    const value = params.get(key);
    if (value) filters.set(key, value);
  }
  const listQuery = new URLSearchParams(filters);
  listQuery.set('page', String(page));
  listQuery.set('pageSize', String(PageSize));
  const rows = useApi<Page>(['clash-collisions', 'report-rows', runId, listQuery.toString()], `/api/clash/reports/${runId}/collisions?${listQuery}`);

  const view = report.data;
  if (report.error) return <Alert color="red" title="Отчёт недоступен">{report.error.message}</Alert>;
  if (!view) return null;

  const run = view.run;
  const s = view.summary;
  const at = formatFullDateTime(run.finishedAt);
  const total = rows.data?.total ?? 0;
  const disciplines = [...new Set(view.models.map((m) => m.discipline))].sort();

  const exportFile = async (format: 'xlsx' | 'html') => {
    const query = new URLSearchParams(filters);
    query.set('baseUrl', window.location.origin);
    setExporting(format);
    try {
      const day = at.slice(0, 10);
      await downloadFile(`/api/clash/reports/${runId}/export.${format}?${query}`,
        `Коллизии ${[view.check.object, view.check.name, day].filter(Boolean).join(' ')}.${format}`);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось выгрузить', message: (error as Error).message });
    } finally {
      setExporting(null);
    }
  };

  const copyChat = async () => {
    setCopying(true);
    try {
      const chat = await apiFetch<{ text: string }>(`/api/clash/reports/${runId}/chat?baseUrl=${encodeURIComponent(window.location.origin)}`);
      if (await copyText(chat.text)) setCopied(true);
      else notifications.show({ color: 'red', message: 'Браузер не дал скопировать текст' });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    } finally {
      setCopying(false);
    }
  };

  return (
    <Stack gap="sm">
      <Breadcrumbs>
        <Anchor component={RouterLink} to="/clash/checks" size="sm">Проверки</Anchor>
        <Text size="sm" c="dimmed">{view.check.object ?? 'Без проекта'}</Text>
        <Anchor component={RouterLink} to={`/clash/checks/${run.partId}`} size="sm">{view.check.name}</Anchor>
        <Text size="sm">Отчёт от {at}</Text>
      </Breadcrumbs>

      {view.newerRunId && (
        <Alert color="yellow" title={`Есть более новый прогон от ${formatFullDateTime(view.newerRunAt)}`}>
          Этот отчёт показывает изменения своего прогона; статусы в строках — текущие.{' '}
          <Anchor component={RouterLink} to={`/clash/reports/${view.newerRunId}?${filters}`}>Открыть новый отчёт</Anchor>
        </Alert>
      )}

      <Group justify="space-between" align="flex-start" wrap="wrap">
        <Stack gap={2}>
          <Title order={2}>Отчёт от {at}</Title>
          <Text size="sm" c="dimmed">
            v{run.version} · {run.trigger === 'schedule' ? 'по расписанию' : 'вручную'}{run.startedBy ? ` (${run.startedBy})` : ''} · поиск {formatDuration(run.searchSeconds)}
          </Text>
        </Stack>
        <Group gap="xs">
          <Button size="xs" variant="default" loading={exporting === 'xlsx'} onClick={() => exportFile('xlsx')}>Excel</Button>
          <Button size="xs" variant="default" loading={exporting === 'html'} onClick={() => exportFile('html')}>HTML</Button>
          {can('Operator') && <Button size="xs" loading={copying} onClick={copyChat}>{copied ? 'Скопировано' : 'Скопировать сообщение для чата'}</Button>}
        </Group>
      </Group>

      <Panel>
        <Spoiler maxHeight={0} showLabel={`Модели (${view.models.length})`} hideLabel="Скрыть модели">
          <Stack gap={2}>
            {view.models.map((m) => (
              <Text key={m.name} size="sm" c={m.unchecked ? 'red' : undefined}>
                {m.name} · {m.discipline} · выгружена {formatFullDateTime(m.uploadedAt)}{m.unchecked ? ' · не выгрузилась, не проверена' : ''}
              </Text>
            ))}
          </Stack>
        </Spoiler>
        <SimpleGrid cols={{ base: 2, sm: 4, lg: 7 }} mt="sm" data-testid="report-tiles">
          <Tile label="Новых" value={s.new} color="red" />
          <Tile label="Вернулись" value={s.returned} color="grape" />
          <Tile label="Активных" value={s.active} color="orange" />
          <Tile label="Исправлено" value={s.fixed} color="green" />
          <Tile label="Ушли из-за правил" value={s.goneByRules} />
          <Tile label="Не проверено" value={s.unchecked} color="yellow.8" />
          <Tile label="Фиктивных" value={s.fake} />
        </SimpleGrid>
      </Panel>

      {view.pairs.length > 0 && (
        <Panel>
          <Table highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Пара разделов</Table.Th>
                <Table.Th ta="right">Открытых сейчас</Table.Th>
                <Table.Th ta="right">Новых</Table.Th>
                <Table.Th ta="right">Вернулись</Table.Th>
                <Table.Th ta="right">Исправлено</Table.Th>
                <Table.Th ta="right">Фиктивных</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {view.pairs.map((p) => (
                <Table.Tr key={p.pair} bg={params.get('pair') === p.pair ? 'var(--mantine-primary-color-light)' : undefined}>
                  <Table.Td>
                    <UnstyledButton aria-label={`Пара ${p.pair}`} onClick={() => update({ pair: params.get('pair') === p.pair ? null : p.pair })}>
                      <Text size="sm" c="blue">{p.pair}</Text>
                    </UnstyledButton>
                  </Table.Td>
                  <Table.Td ta="right">{n(p.open)}</Table.Td>
                  <Table.Td ta="right">{n(p.new)}</Table.Td>
                  <Table.Td ta="right">{n(p.returned)}</Table.Td>
                  <Table.Td ta="right">{n(p.fixed)}</Table.Td>
                  <Table.Td ta="right">{n(p.fake)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
      )}

      <Panel>
        <Stack gap="xs">
          <Group gap="xs" wrap="wrap">
            <SegmentedControl size="xs" value={scope} onChange={(v) => update({ scope: v === 'open' ? 'open' : null })}
              data={[{ value: 'changes', label: 'Изменения прогона' }, { value: 'open', label: 'Открытые сейчас' }]} />
            <Select size="xs" w={110} placeholder="Раздел" clearable data={disciplines} value={params.get('discipline')}
              onChange={(v) => update({ discipline: v })} />
            <Select size="xs" w={150} placeholder="Пара разделов" clearable data={view.pairs.map((p) => p.pair)} value={params.get('pair')}
              onChange={(v) => update({ pair: v })} />
            <Select size="xs" w={170} placeholder="Статус" clearable value={params.get('status')} onChange={(v) => update({ status: v })}
              data={(Object.keys(statusLabel) as CollisionStatus[]).map((k) => ({ value: k, label: statusLabel[k].label }))} />
            <Select size="xs" w={140} placeholder="Кто правит" clearable value={params.get('assignee')} onChange={(v) => update({ assignee: v })}
              data={[{ value: 'Both', label: 'обе стороны' }, { value: 'SideA', label: 'сторона A' }, { value: 'SideB', label: 'сторона B' }]} />
            <Select size="xs" w={200} placeholder="Модель" clearable searchable data={view.models.map((m) => m.name)} value={params.get('model')}
              onChange={(v) => update({ model: v })} />
            <TextInput key={`${runId}|level|${params.get('level') ?? ''}`} size="xs" w={120} placeholder="Уровень" defaultValue={params.get('level') ?? ''}
              onBlur={(e) => update({ level: e.currentTarget.value.trim() || null })}
              onKeyDown={(e) => { if (e.key === 'Enter') update({ level: e.currentTarget.value.trim() || null }); }} />
            <TextInput key={`${runId}|q|${params.get('q') ?? ''}`} size="xs" w={160} placeholder="№, ID или UniqueId" defaultValue={params.get('q') ?? ''}
              onBlur={(e) => update({ q: e.currentTarget.value.trim() || null })}
              onKeyDown={(e) => { if (e.key === 'Enter') update({ q: e.currentTarget.value.trim() || null }); }} />
          </Group>
          <Text size="xs" c="dimmed">{n(total)} коллизий</Text>
          <ReportRows rows={rows.data?.items ?? []} selected={collisionId} onOpen={(id) => update({ collision: id }, false)} />
          {rows.data && rows.data.items.length === 0 && <Text size="sm" c="dimmed">Коллизий с такими условиями нет</Text>}
          {total > PageSize && (
            <Pagination size="sm" total={Math.ceil(total / PageSize)} value={page} onChange={(p) => update({ page: String(p) }, false)} />
          )}
        </Stack>
      </Panel>

      <ReportCollisionDrawer partId={run.partId} collisionId={collisionId} canEdit={canWork} canMerge={can('Operator')}
        onClose={() => update({ collision: null }, false)} />
    </Stack>
  );
}
