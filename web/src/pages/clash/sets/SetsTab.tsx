import { useEffect, useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Group, Menu, Modal, MultiSelect, NavLink, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconDots } from '@tabler/icons-react';
import { useApi, useApiPost } from '../../../api/queries';
import type { DraftView, ModelCatalog, PresenceView, SetPreviewResult, UnassignedResult } from '../../../api/types';
import { askConfirm } from '../../../components/ui';
import { categoryLabel } from '../../../lib/categories';
import {
  type ClashConfig, copySet, type GroupSource, newGroup, newSet, removeGroup, removeSet, replaceGroup, replaceSet, type SetGroup,
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

function SetRow({ partId, config, setId, name, active, onClick }: { partId: string; config: ClashConfig; setId: string; name: string; active: boolean; onClick: () => void }) {
  const [debounced] = useDebouncedValue(config, 400);
  const preview = useApiPost<SetPreviewResult>(['clash-preview', partId, setId, JSON.stringify(debounced)], `/api/clash/parts/${partId}/preview`,
    { configuration: debounced, setId });
  return (
    <NavLink label={name || 'без имени'} active={active} onClick={onClick}
      rightSection={<Text size="xs" c="green">{preview.data ? preview.data.matched.toLocaleString('ru-RU') : '…'}</Text>} />
  );
}

function Unassigned({ partId, config, groupId }: { partId: string; config: ClashConfig; groupId: string }) {
  const [debounced] = useDebouncedValue(config, 400);
  const [open, setOpen] = useState(false);
  const result = useApiPost<UnassignedResult>(['clash-unassigned', partId, groupId, JSON.stringify(debounced)], `/api/clash/parts/${partId}/unassigned`,
    { configuration: debounced, groupId });
  if (!result.data || result.data.count === 0) return null;
  return (
    <Alert color="orange" p="xs">
      <Text size="xs">{result.data.count.toLocaleString('ru-RU')} элементов источника не попали ни в один набор группы.</Text>
      <Button size="compact-xs" variant="subtle" color="orange" onClick={() => setOpen(!open)}>{open ? 'Скрыть' : 'Показать по категориям'}</Button>
      {open && result.data.byCategory.map((c) => <Text key={c.value} size="xs">{categoryLabel(c.value)} — {c.count.toLocaleString('ru-RU')}</Text>)}
    </Alert>
  );
}

/** Наборы: группы (разделы и свои), наборы выбранной группы со счётчиками и редактор набора. */
export function SetsTab({ partId, view, config, readOnly, onChange, onOpenSet, present }: {
  partId: string;
  view: DraftView;
  config: ClashConfig;
  readOnly: boolean;
  onChange: Change;
  onOpenSet: (setId: string | null) => void;
  present: PresenceView[];
}) {
  const [groupId, setGroupId] = useState<string | null>(config.groups[0]?.id ?? null);
  const [setId, setSetId] = useState<string | null>(null);
  const [dialog, setDialog] = useState<SetGroup | 'new' | null>(null);
  const modelIds = view.models.map((m) => `modelId=${m.modelKey}`).join('&');
  const catalog = useApi<ModelCatalog>(['clash-models', 'catalog', partId, modelIds], `/api/clash/catalog${modelIds ? `?${modelIds}` : ''}`);

  const group = config.groups.find((g) => g.id === groupId) ?? config.groups[0];
  const sets = config.sets.filter((s) => s.groupId === group?.id);
  const set = sets.find((s) => s.id === setId) ?? sets[0];
  useEffect(() => onOpenSet(set?.id ?? null), [set?.id, onOpenSet]);

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
            description={isDisciplineGroup(g) ? undefined : 'своя группа'}
            rightSection={<Badge size="xs" variant="light" color="gray">{config.sets.filter((s) => s.groupId === g.id).length}</Badge>} />
        ))}
        {!readOnly && <Button size="compact-sm" variant="subtle" onClick={() => setDialog('new')}>+ Своя группа</Button>}
      </Stack>

      {group && (
        <Stack gap={4} w={240} style={{ flexShrink: 0 }}>
          <Group justify="space-between">
            <Text size="xs" fw={700} c="dimmed" tt="uppercase">Наборы «{group.name}»</Text>
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
          {sets.map((s) => (
            <SetRow key={s.id} partId={partId} config={config} setId={s.id} name={s.name} active={s.id === set?.id} onClick={() => setSetId(s.id)} />
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
          {isDisciplineGroup(group) && <Unassigned partId={partId} config={config} groupId={group.id} />}
        </Stack>
      )}

      {set && group ? (
        <SetEditor partId={partId} config={config} set={set} source={sourceText(group, view)} catalog={catalog.data} readOnly={readOnly}
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
