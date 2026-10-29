import { useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { Alert, Anchor, Avatar, Badge, Breadcrumbs, Button, Group, List, Menu, Modal, SegmentedControl, Stack, Tabs, Text, TextInput, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { ClashPartView, ClashTemplateView, DraftView, PresenceView } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { askConfirm, Panel } from '../../../components/ui';
import { DraftTab } from '../draft/DraftTab';
import { ExclusionsTab } from '../matrix/ExclusionsTab';
import { MatrixTab } from '../matrix/MatrixTab';
import { SetsTab } from '../sets/SetsTab';
import { ModelsTab } from './ModelsTab';
import { type Conflicts, useDraft } from './useDraft';

export type PartTab = 'models' | 'sets' | 'matrix' | 'exclusions' | 'draft';

const tabNames: Record<string, string> = { models: 'модели', sets: 'наборы', matrix: 'матрица', exclusions: 'исключения', draft: 'черновик' };

/** Кто ещё открыл часть и где он. */
function PresenceBadges({ present, me }: { present: PresenceView[]; me: string | undefined }) {
  const others = present.filter((p) => p.userId !== me);
  if (others.length === 0) return null;
  return (
    <Group gap={6}>
      {others.map((p) => (
        <Tooltip key={p.userId} label={`${p.displayName} — ${tabNames[p.tab] ?? p.tab}`}>
          <Badge variant="light" color="teal" leftSection={<Avatar size={16} radius="xl" color="teal">{p.displayName[0]}</Avatar>}>
            {p.displayName} — {tabNames[p.tab] ?? p.tab}
          </Badge>
        </Tooltip>
      ))}
    </Group>
  );
}

/** Двое изменили одно и то же: по каждому элементу — взять чужое или оставить своё. */
function ConflictDialog({ conflicts, onResolve, onClose }: { conflicts: Conflicts; onResolve: (theirs: string[]) => void; onClose: () => void }) {
  const [choice, setChoice] = useState<Record<string, 'mine' | 'theirs'>>({});
  return (
    <Modal opened onClose={onClose} title="Эти элементы уже изменил другой пользователь" size="lg">
      <Stack gap="sm">
        {conflicts.items.map((c) => (
          <Group key={c.key} justify="space-between" wrap="nowrap">
            <Text size="sm">{c.label}</Text>
            <SegmentedControl size="xs" value={choice[c.key] ?? 'mine'} onChange={(v) => setChoice({ ...choice, [c.key]: v as 'mine' | 'theirs' })}
              data={[{ value: 'mine', label: 'Оставить своё' }, { value: 'theirs', label: 'Взять чужое' }]} />
          </Group>
        ))}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Отмена</Button>
          <Button onClick={() => onResolve(conflicts.items.filter((c) => choice[c.key] === 'theirs').map((c) => c.key))}>Сохранить</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Часть проекта: шапка с сохранением и присутствием, вкладки модели, наборы, матрица, исключения, черновик. */
export function PartShell() {
  const { id } = useParams();
  const partId = id!;
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { can, user } = useAuth();
  const readOnly = !can('Operator');
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as PartTab) ?? 'sets';
  const [openSet, setOpenSet] = useState<string | null>(null);
  const [templateName, setTemplateName] = useState('');
  const part = useApi<ClashPartView>(['clash-parts', partId], `/api/clash/parts/${partId}`);
  const templates = useApi<ClashTemplateView[]>(['clash-templates'], '/api/clash/templates');
  const presence = useApi<PresenceView[]>(['clash-presence', partId], `/api/clash/parts/${partId}/presence`);
  const draft = useDraft(partId);

  // Отметка «я здесь»: раз в 15 с и при смене вкладки или набора; уход со страницы — явный выход.
  useEffect(() => {
    const touch = () => apiFetch(`/api/clash/parts/${partId}/presence`, { method: 'POST', json: { tab, setId: tab === 'sets' ? openSet : null } }).catch(() => {});
    touch();
    const timer = setInterval(touch, 15_000);
    return () => clearInterval(timer);
  }, [partId, tab, openSet]);
  useEffect(() => () => void apiFetch(`/api/clash/parts/${partId}/presence`, { method: 'DELETE' }).catch(() => {}), [partId]);

  // Несохранённые правки не теряются молча при закрытии или перезагрузке вкладки.
  useEffect(() => {
    if (!draft.dirty) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [draft.dirty]);

  if (!part.data || !draft.view || !draft.local) return null;
  const config = draft.local;
  const leave = (to: string) => (draft.dirty ? askConfirm('Есть несохранённые изменения. Уйти без сохранения?', () => navigate(to)) : navigate(to));

  async function act(path: string, success: string, body?: unknown) {
    try {
      await apiFetch<DraftView>(path, { method: 'POST', json: body });
      notifications.show({ color: 'green', message: success });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
      await queryClient.invalidateQueries({ queryKey: ['clash-templates'] });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  return (
    <Stack gap="sm">
      <Breadcrumbs>
        <Anchor component="button" size="sm" onClick={() => leave('/clash')}>Коллизии</Anchor>
        <Text size="sm" c="dimmed">{part.data.object ?? 'Без проекта'}</Text>
        <Text size="sm">{part.data.name}</Text>
      </Breadcrumbs>
      <Group justify="space-between" wrap="wrap">
        <Group gap="md">
          <Text fw={700} size="xl">{part.data.name}</Text>
          {draft.dirty && <Text size="sm" c="orange">● есть несохранённые изменения</Text>}
          <PresenceBadges present={presence.data ?? []} me={user?.id} />
        </Group>
        {!readOnly && (
          <Group gap="xs">
            <Menu>
              <Menu.Target><Button size="xs" variant="default">Шаблоны</Button></Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>Применить к этой части</Menu.Label>
                {(templates.data ?? []).map((t) => (
                  <Menu.Item key={t.id} onClick={() => askConfirm(`Заменить черновик части шаблоном «${t.name}»?`,
                    () => act(`/api/clash/templates/${t.id}/apply/${partId}`, `Применён шаблон «${t.name}»`), 'blue')}>
                    {t.name} <Text span size="xs" c="dimmed">({t.sets} наборов)</Text>
                  </Menu.Item>
                ))}
                {(templates.data ?? []).length === 0 && <Menu.Item disabled>Шаблонов нет</Menu.Item>}
                <Menu.Divider />
                <Stack gap={4} p="xs">
                  <TextInput size="xs" placeholder="Название шаблона" value={templateName} onChange={(e) => setTemplateName(e.currentTarget.value)} />
                  <Button size="xs" disabled={!templateName.trim() || draft.dirty}
                    onClick={() => act('/api/clash/templates', `Шаблон «${templateName}» сохранён`, { name: templateName, partId })}>
                    Сохранить черновик как шаблон
                  </Button>
                </Stack>
              </Menu.Dropdown>
            </Menu>
            <Button size="xs" variant="default" disabled={!draft.dirty} onClick={draft.reset}>Отменить правки</Button>
            <Button size="xs" disabled={!draft.dirty} loading={draft.saving} onClick={draft.save}>Сохранить</Button>
          </Group>
        )}
      </Group>
      {draft.problems.length > 0 && (
        <Alert color="red" title="Черновик нельзя прогнать">
          <List size="sm">{draft.problems.map((p) => <List.Item key={p}>{p}</List.Item>)}</List>
        </Alert>
      )}
      <Panel>
        <Tabs value={tab} onChange={(v) => setParams({ tab: v ?? 'sets' })} keepMounted={false}>
          <Tabs.List mb="md">
            <Tabs.Tab value="models">Модели ({part.data.models.length})</Tabs.Tab>
            <Tabs.Tab value="sets">Наборы</Tabs.Tab>
            <Tabs.Tab value="matrix">Матрица</Tabs.Tab>
            <Tabs.Tab value="exclusions">Исключения</Tabs.Tab>
            <Tabs.Tab value="draft">Черновик</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="models"><ModelsTab part={part.data} readOnly={readOnly} /></Tabs.Panel>
          <Tabs.Panel value="sets">
            <SetsTab partId={partId} view={draft.view} config={config} readOnly={readOnly} onChange={draft.edit} onOpenSet={setOpenSet}
              present={(presence.data ?? []).filter((p) => p.userId !== user?.id)} />
          </Tabs.Panel>
          <Tabs.Panel value="matrix">
            <MatrixTab config={config} readOnly={readOnly} onChange={draft.edit}
              onFill={() => act(`/api/clash/parts/${partId}/draft/matrix/fill`, 'Матрица заполнена')} />
          </Tabs.Panel>
          <Tabs.Panel value="exclusions"><ExclusionsTab config={config} readOnly={readOnly} onChange={draft.edit} /></Tabs.Panel>
          <Tabs.Panel value="draft">
            <DraftTab partId={partId} readOnly={readOnly} dirty={draft.dirty} onSave={draft.save} />
          </Tabs.Panel>
        </Tabs>
      </Panel>
      {draft.conflicts && <ConflictDialog conflicts={draft.conflicts} onResolve={draft.resolve} onClose={draft.dismiss} />}
    </Stack>
  );
}
