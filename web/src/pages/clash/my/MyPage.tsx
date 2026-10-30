import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { Box, Button, Card, Group, ScrollArea, Select, SimpleGrid, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { CheckView, MyCollisionPage, MySummary } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { Empty, PageTitle } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime } from '../../../lib/format';
import { Side } from '../approved/ApprovedTab';
import { CollisionCard } from '../approved/CollisionCard';
import { StatusBadge } from '../approved/StatusBadge';
import { HitScene } from '../viewer/HitScene';

type Scope = 'tofix' | 'taken' | 'new';

const n = (value: number) => value.toLocaleString('ru-RU');
const PageSize = 50;

const statusOptions = [
  { value: 'open', label: 'Открытые' },
  { value: 'fake', label: 'Фиктивные' },
  { value: 'fixed', label: 'Исправленные' },
  { value: 'gone', label: 'Ушли из-за правил' },
  { value: 'all', label: 'Все' },
];

function Counter({ label, value, active, onClick }: { label: string; value: number | undefined; active: boolean; onClick: () => void }) {
  return (
    <UnstyledButton onClick={onClick} aria-label={`${label}: ${value ?? 0}`}>
      <Card withBorder padding="sm" radius="md" bg={active ? 'var(--mantine-primary-color-light)' : undefined}>
        <Text size="xs" c="dimmed">{label}</Text>
        <Text fw={700} size="xl">{value == null ? '—' : n(value)}</Text>
      </Card>
    </UnstyledButton>
  );
}

/**
 * «Мои коллизии»: коллизии разделов профиля по всем утверждённым проверкам — та же выборка, что у плагина. Счётчики
 * переключают «Мне править», «Взяла другая сторона» и «Новые с прошлого захода»; справа — 3D и карточка, ↑/↓ листают.
 */
export function MyPage() {
  const queryClient = useQueryClient();
  const { can, canWork } = useAuth();
  const [params, setParams] = useSearchParams();
  const [scope, setScope] = useState<Scope>('tofix');
  const [status, setStatus] = useState('open');
  const [project, setProject] = useState<string | null>(null);
  const [partId, setPartId] = useState<string | null>(null);
  const [pair, setPair] = useState('');
  const [level, setLevel] = useState('');
  const [page, setPage] = useState(1);
  const collisionId = params.get('collision');
  const summary = useApi<MySummary>(['clash-my', 'summary'], '/api/clash/my/summary');
  const checks = useApi<CheckView[]>(['clash-checks'], '/api/clash/checks');

  // Заход отмечается при открытии страницы; новые считаются от прошлой отметки, поэтому сразу не обнуляются.
  useEffect(() => {
    apiFetch('/api/clash/my/visit', { method: 'POST' })
      .then(() => queryClient.invalidateQueries({ queryKey: ['clash-my', 'summary'] }))
      .catch(() => {});
  }, [queryClient]);

  const select = useCallback((id: string | null) =>
    setParams((p) => {
      const next = new URLSearchParams(p);
      if (id) next.set('collision', id); else next.delete('collision');
      return next;
    }, { replace: true }), [setParams]);

  const search = new URLSearchParams({ scope: scope === 'new' ? 'all' : scope, status, page: String(page), pageSize: String(PageSize) });
  if (scope === 'new') search.set('since', 'visit');
  if (project) search.set('object', project);
  if (partId) search.set('partId', partId);
  if (pair.trim()) search.set('pair', pair.trim());
  if (level.trim()) search.set('level', level.trim());
  const list = useApi<MyCollisionPage>(['clash-my', 'list', search.toString()], `/api/clash/my?${search}`);
  const items = useMemo(() => list.data?.items ?? [], [list.data]);
  const index = items.findIndex((r) => r.collision.id === collisionId);
  const current = index >= 0 ? items[index] : null;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
      const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0;
      if (!step || items.length === 0) return;
      e.preventDefault();
      select(items[Math.min(items.length - 1, Math.max(0, index + step))].collision.id);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [items, index, select]);

  const switchScope = (next: Scope) => {
    setPage(1);
    setScope(next);
  };

  if (summary.data && summary.data.disciplines.length === 0)
    return (
      <>
        <PageTitle>{t.clash.myTitle}</PageTitle>
        <Empty>Разделы не указаны — попросите координатора указать их в профиле</Empty>
      </>
    );

  const total = list.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PageSize));
  const projects = [...new Set((checks.data ?? []).map((c) => c.object ?? 'Без проекта'))];
  const parts = (checks.data ?? []).filter((c) => !project || (c.object ?? 'Без проекта') === project);

  return (
    <Stack gap="sm">
      <PageTitle>{t.clash.myTitle}</PageTitle>
      <SimpleGrid cols={3} spacing="sm" maw={720}>
        <Counter label="Мне править" value={summary.data?.toFix} active={scope === 'tofix'} onClick={() => switchScope('tofix')} />
        <Counter label="Взяла другая сторона" value={summary.data?.takenByOther} active={scope === 'taken'} onClick={() => switchScope('taken')} />
        <Counter label="Новые с прошлого захода" value={summary.data?.newSinceVisit} active={scope === 'new'} onClick={() => switchScope('new')} />
      </SimpleGrid>
      <Group gap="xs" wrap="wrap">
        <Text size="sm" c="dimmed">Разделы: {summary.data?.disciplines.join(', ') ?? ''}</Text>
        <Select size="xs" w={160} placeholder="Проект" clearable data={projects} value={project}
          onChange={(v) => { setPage(1); setProject(v); setPartId(null); }} aria-label="Проект" />
        <Select size="xs" w={200} placeholder="Проверка" clearable data={parts.map((c) => ({ value: c.partId, label: c.name }))} value={partId}
          onChange={(v) => { setPage(1); setPartId(v); }} aria-label="Проверка" />
        <Select size="xs" w={170} data={statusOptions} value={status} allowDeselect={false}
          onChange={(v) => { setPage(1); setStatus(v ?? 'open'); }} aria-label="Статус" />
        <TextInput size="xs" w={140} placeholder="Пара: КР × ОВК" value={pair} onChange={(e) => { setPage(1); setPair(e.currentTarget.value); }} aria-label="Пара разделов" />
        <TextInput size="xs" w={120} placeholder="Уровень" value={level} onChange={(e) => { setPage(1); setLevel(e.currentTarget.value); }} aria-label="Уровень" />
      </Group>

      <Group align="stretch" gap={0} wrap="nowrap" h="calc(100vh - 330px)" mih={560}
        style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
        <Stack gap={0} w={{ base: 380, xl: 440 }} style={{ flexShrink: 0, borderRight: '1px solid var(--mantine-color-default-border)' }}>
          <ScrollArea style={{ flex: 1 }} type="auto">
            {items.map((r) => (
              <UnstyledButton key={r.collision.id} w="100%" px="xs" py={4} onClick={() => select(r.collision.id)} aria-label={`Коллизия ${r.collision.number}`}
                bg={r.collision.id === collisionId ? 'var(--mantine-primary-color-light)' : undefined}
                style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
                <Group gap={6} wrap="nowrap">
                  <Stack gap={2} w={70}>
                    <Text size="xs" c="dimmed">№ {r.collision.number}</Text>
                    <StatusBadge status={r.collision.status} />
                  </Stack>
                  <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                    <Text size="xs" fw={600} truncate>{r.partName} · {r.object ?? 'Без проекта'}</Text>
                    <Side row={r.collision} side="A" />
                    <Side row={r.collision} side="B" />
                    {r.takenBy && !r.mineToFix && (
                      <Text size="xs" c="dimmed">взял {r.takenBy}{r.takenAt ? `, ${formatDateTime(r.takenAt)}` : ''}</Text>
                    )}
                  </Stack>
                  <Text size="xs">{Math.round(r.collision.depthMm)} мм</Text>
                </Group>
              </UnstyledButton>
            ))}
            {list.data && items.length === 0 && <Text size="sm" c="dimmed" p="sm">Коллизий нет</Text>}
          </ScrollArea>
          <Group justify="space-between" px="xs" py={4} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
            <Text size="xs" c="dimmed">{n(total)} коллизий · ↑ ↓ листать</Text>
            {pages > 1 && (
              <Group gap={4}>
                <Button size="compact-xs" variant="subtle" disabled={page <= 1} onClick={() => setPage(page - 1)}>←</Button>
                <Text size="xs">{page} / {pages}</Text>
                <Button size="compact-xs" variant="subtle" disabled={page >= pages} onClick={() => setPage(page + 1)}>→</Button>
              </Group>
            )}
          </Group>
        </Stack>

        <Stack gap={0} p="xs" style={{ flex: 1, minWidth: 480 }}>
          {collisionId ? (
            <>
              <Box style={{ flex: 3, minHeight: 0 }}>
                <HitScene runId="" number={current?.collision.number ?? 0} collisionId={collisionId}
                  title={current ? `№ ${current.collision.number} · ${current.partName} · глубина ${Math.round(current.collision.depthMm)} мм` : undefined} />
              </Box>
              <Box pt="xs" style={{ flex: 2, minHeight: 0 }}>
                <CollisionCard key={collisionId} collisionId={collisionId} canEdit={canWork} canMerge={can('Operator')} />
              </Box>
            </>
          ) : (
            <Empty>Выберите коллизию в списке</Empty>
          )}
        </Stack>
      </Group>
    </Stack>
  );
}
