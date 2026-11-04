import { useEffect, useMemo, useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Group, Menu, Modal, MultiSelect, NavLink, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconDots } from '@tabler/icons-react';
import { useApi, useApiPost } from '../../../api/queries';
import type { DraftView, ModelCatalog, PresenceView, SetPreviewBatch, UnassignedResult } from '../../../api/types';
import { askConfirm } from '../../../components/ui';
import { categoryLabel } from '../../../lib/categories';
import {
  type ClashConfig, copySet, type GroupSource, hashConfig, newGroup, newSet, removeGroup, removeSet, replaceGroup, replaceSet, type SetGroup,
} from '../../../lib/clashConfig';
import { SetEditor } from './SetEditor';

type Change = (change: (config: ClashConfig) => ClashConfig) => void;

/** Группа раздела создаётся сама: её не переименовать и не удалить, источник — модели раздела. */
const isDisciplineGroup = (g: SetGroup) => g.source.modelKeys.length === 0 && g.source.disciplines.length === 1 && g.source.disciplines[0] === g.name;

function sourceText(group: SetGroup, view: DraftView) {
  const models = group.source.modelKeys.map((k) => view.models.find((m) => m.modelKey === k)?.name ?? 'модель не в части');
  const parts = [...group.source.disciplines.map((d) => `модели раздела ${d}`), ...models];
  return parts.length ? parts.join(', ') : 'не задано';
}

/** Своя группа: имя и откуда она берёт элементы — разделы части и (или) конкретные модели. */
function GroupDialog({ view, group, onSave, onClose }: { view: DraftView; group: SetGroup | null; onSave: (name: string, source: GroupSource) => void; onClose: () => void }) {
  const [name, setName] = useState(group?.name ?? '');
  const [disciplines, setDisciplines] = useState<string[]>(group?.source.disciplines ?? []);
  const [models, setModels] = useState<string[]>(group?.source.modelKeys ?? []);
  return (
    <Modal opened onClose={onClose} title={group ? 'Группа' : 'Своя группа'}>
      <Stack gap="sm">
        <TextInput label="Имя группы" placeholder="например, Проезды" value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <MultiSelect label="Элементы из моделей разделов" data={view.disciplines} value={disciplines} onChange={setDisciplines} />
        <MultiSelect label="или из конкретных моделей" data={view.models.map((m) => ({ value: m.modelKey, label: m.name }))} value={models} onChange={setModels} />
        <Text size="xs" c="dimmed">Группа — отдельная строка и столбец матрицы. Её наборы отбирают элементы только из этих моделей.</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Отмена</Button>
          <Button disabled={!name.trim() || (disciplines.length === 0 && models.length === 0)}
            onClick={() => onSave(name.trim(), { disciplines, modelKeys: models })}>
            {group ? 'Сохранить' : 'Добавить'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Счётчики наборов группы и «не попали» — одним запросом на версию черновика (через 400 мс после последней правки). */
function useGroupPreview(partId: string, config: ClashConfig, group: SetGroup | undefined) {
  const [debounced] = useDebouncedValue(config, 400);
  // Наборы — из отложенного черновика: только что добавленного набора сервер в нём ещё не увидит.
  const setIds = group ? debounced.sets.filter((s) => s.groupId === group.id).map((s) => s.id) : [];
  const body = group ? { configuration: debounced, setIds, groupId: isDisciplineGroup(group) ? group.id : undefined } : null;
  return useApiPost<SetPreviewBatch>(['clash-preview-batch', partId, group?.id, hashConfig(debounced)],
    `/api/clash/parts/${partId}/draft/sets/preview/batch`, body, { gcTime: 30_000 });
}

function SetRow({ name, count, active, onClick }: { name: string; count: number | undefined; active: boolean; onClick: () => void }) {
  return (
    <NavLink label={name || 'без имени'} active={active} onClick={onClick}
      rightSection={<Text size="xs" c="green">{count != null ? count.toLocaleString('ru-RU') : '…'}</Text>} />
  );
}

function Unassigned({ result }: { result: UnassignedResult | null | undefined }) {
  const [open, setOpen] = useState(false);
  if (!result || result.count === 0) return null;
  return (
    <Alert color="orange" p="xs">
      <Text size="xs">{result.count.toLocaleString('ru-RU')} элементов источника не попали ни в один набор группы.</Text>
      <Button size="compact-xs" variant="subtle" color="orange" onClick={() => setOpen(!open)}>{open ? 'Скрыть' : 'Показать по категориям'}</Button>
      {open && result.byCategory.map((c) => <Text key={c.value} size="xs">{categoryLabel(c.value)} — {c.count.toLocaleString('ru-RU')}</Text>)}
    </Alert>
  );
}

/** Наборы: группы (разделы и свои), наборы выбранной группы со счётчиками и редактор набора. */
export function SetsTab({ partId, view, config, readOnly, onChange, onOpenSet, present, initialSet }: {
  partId: string;
  view: DraftView;
  config: ClashConfig;
  readOnly: boolean;
  onChange: Change;
  onOpenSet: (setId: string | null) => void;
  present: PresenceView[];
  /** Набор, открытый сразу (переход из черновика `?tab=sets&set={id}`). */
  initialSet?: string | null;
}) {
  const [groupId, setGroupId] = useState<string | null>(config.sets.find((s) => s.id === initialSet)?.groupId ?? config.groups[0]?.id ?? null);
  const [setId, setSetId] = useState<string | null>(initialSet ?? null);
  const [dialog, setDialog] = useState<SetGroup | 'new' | null>(null);
  const modelIds = view.models.map((m) => `modelId=${m.modelKey}`).join('&');
  const catalog = useApi<ModelCatalog>(['clash-models', 'catalog', partId, modelIds], `/api/clash/catalog${modelIds ? `?${modelIds}` : ''}`);

  const group = config.groups.find((g) => g.id === groupId) ?? config.groups[0];
  const sets = config.sets.filter((s) => s.groupId === group?.id);
  const set = sets.find((s) => s.id === setId) ?? sets[0];
  const preview = useGroupPreview(partId, config, group);
  useEffect(() => onOpenSet(set?.id ?? null), [set?.id, onOpenSet]);

  // Группа раздела, моделей которого в части больше нет (осталась от старого состава или шаблона), — удаляется как своя.
  const orphan = (g: SetGroup) => isDisciplineGroup(g) && !view.disciplines.some((d) => d.toLowerCase() === g.source.disciplines[0].toLowerCase());
  const modelNames = useMemo(() => Object.fromEntries(view.models.map((m) => [m.modelKey.toLowerCase(), m.name])), [view.models]);

  const addSet = () => {
    const created = newSet(group!.id, `Набор ${sets.length + 1}`);
    onChange((c) => ({ ...c, sets: [...c.sets, created] }));
    setSetId(created.id);
  };

  return (
    <Group align="flex-start" gap="md" wrap="nowrap">
      <Stack gap={4} w={200} style={{ flexShrink: 0 }}>
        <Text size="xs" fw={700} c="dimmed" tt="uppercase">Группы</Text>
        {config.groups.map((g) => (
          <NavLink key={g.id} label={g.name} active={g.id === group?.id} onClick={() => { setGroupId(g.id); setSetId(null); }}
            description={!isDisciplineGroup(g) ? 'своя группа' : orphan(g) ? 'раздела нет в части' : undefined}
            c={orphan(g) ? 'orange' : undefined}
            rightSection={<Badge size="xs" variant="light" color="gray">{config.sets.filter((s) => s.groupId === g.id).length}</Badge>} />
        ))}
        {!readOnly && <Button size="compact-sm" variant="subtle" onClick={() => setDialog('new')}>+ Своя группа</Button>}
      </Stack>

      {group && (
        <Stack gap={4} w={240} style={{ flexShrink: 0 }}>
          <Group justify="space-between">
            <Text size="xs" fw={700} c="dimmed" tt="uppercase">Наборы «{group.name}»</Text>
            {!readOnly && orphan(group) && (
              <Button size="compact-xs" variant="subtle" color="red"
                onClick={() => askConfirm(`Удалить группу «${group.name}» с её наборами?`, () => onChange((c) => removeGroup(c, group.id)))}>
                Удалить группу
              </Button>
            )}
            {!readOnly && !isDisciplineGroup(group) && (
              <Menu>
                <Menu.Target><ActionIcon size="sm" variant="subtle" aria-label="Действия группы"><IconDots size={14} /></ActionIcon></Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item onClick={() => setDialog(group)}>Изменить группу</Menu.Item>
                  <Menu.Item color="red" onClick={() => askConfirm(`Удалить группу «${group.name}» с её наборами?`, () => onChange((c) => removeGroup(c, group.id)))}>
                    Удалить группу
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            )}
          </Group>
          {isDisciplineGroup(group) && !orphan(group) && (
            <Text size="xs" c="dimmed">
              Группа раздела есть, пока в части есть его модели. Чтобы не проверять раздел, уберите его модели на вкладке «Модели» или выключите
              клетки раздела в матрице.
            </Text>
          )}
          {sets.map((s) => (
            <SetRow key={s.id} name={s.name} count={preview.data?.counts[s.id]} active={s.id === set?.id} onClick={() => setSetId(s.id)} />
          ))}
          {!readOnly && (
            <Group gap={4}>
              <Button size="compact-xs" variant="subtle" onClick={addSet}>+ Набор</Button>
              {set && <Button size="compact-xs" variant="subtle" onClick={() => onChange((c) => copySet(c, set.id))}>Копировать</Button>}
              {set && (
                <Button size="compact-xs" variant="subtle" color="red"
                  onClick={() => askConfirm(`Удалить набор «${set.name}»?`, () => onChange((c) => removeSet(c, set.id)))}>
                  Удалить
                </Button>
              )}
            </Group>
          )}
          {isDisciplineGroup(group) && <Unassigned result={preview.data?.unassigned} />}
        </Stack>
      )}

      {set && group ? (
        <SetEditor partId={partId} config={config} set={set} source={sourceText(group, view)} catalog={catalog.data} models={modelNames} readOnly={readOnly}
          editing={present.filter((p) => p.tab === 'sets' && p.setId === set.id)} onChange={(changed) => onChange((c) => replaceSet(c, changed))} />
      ) : (
        <Text size="sm" c="dimmed">В группе ещё нет наборов{readOnly ? '' : ' — нажмите «+ Набор»'}</Text>
      )}

      {dialog && (
        <GroupDialog view={view} group={dialog === 'new' ? null : dialog} onClose={() => setDialog(null)}
          onSave={(name, source) => {
            if (dialog === 'new') {
              const created = newGroup(name, source);
              onChange((c) => ({ ...c, groups: [...c.groups, created] }));
              setGroupId(created.id);
            } else onChange((c) => replaceGroup(c, { ...dialog, name, source }));
            setDialog(null);
          }} />
      )}
    </Group>
  );
}
