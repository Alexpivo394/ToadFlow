import { useState } from 'react';
import { Link } from 'react-router';
import { Accordion, Anchor, Autocomplete, Badge, Button, FileButton, Group, Modal, Select, Stack, Table, Text, TextInput, Tooltip } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch, downloadFile } from '../../../api/client';
import { useApi, useApiMutation } from '../../../api/queries';
import type { CheckProfile, LoiProfileDetails, LoiProfileSummary } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { RequireRole } from '../../../auth/RequireRole';
import { ConfirmButton, Empty, Panel } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime, pluralRu } from '../../../lib/format';
import { groupLabel, groupProfiles, newProfile, normalizeProfile, profileDisplayName } from '../../../lib/loi';
import { ProfileEditor } from './ProfileEditor';
import { useLoiCatalog, type LoiCatalog } from './useLoiCatalog';

type Editing = { id: string | null; group: string; profile: CheckProfile };

/** Каталог категорий и параметров: его снимает Revit на агенте — у сервера своего Revit нет. */
function CatalogPanel({ catalog }: { catalog: LoiCatalog }) {
  const years = catalog.overview?.agentYears ?? [];
  const [year, setYear] = useState<string | null>(null);
  const chosen = year ?? (years[0] ? String(years[0]) : null);
  const [runId, setRunId] = useState<string | null>(null);
  const refresh = useApiMutation<void, { runId: string }>('POST', () => '/api/loi/catalog/refresh', {
    invalidate: ['runs'],
    json: () => ({ revitYear: chosen ? Number(chosen) : null }),
  });

  return (
    <Panel
      title="Каталог Revit для редактора правил"
      actions={
        <RequireRole role="Operator">
          <Group gap={6} wrap="nowrap">
            {years.length > 1 && (
              <Select size="xs" w={110} data={years.map((y) => ({ value: String(y), label: `Revit ${y}` }))} value={chosen} allowDeselect={false} onChange={setYear} />
            )}
            <Button size="xs" variant="default" disabled={!chosen} loading={refresh.isPending}
              onClick={() => refresh.mutate(undefined, {
                onSuccess: (result) => {
                  setRunId(result.runId);
                  notifications.show({ color: 'green', message: 'Задание отправлено — каталог появится, когда свободный Revit его снимет (обычно пара минут)' });
                },
              })}>
              Обновить из Revit
            </Button>
          </Group>
        </RequireRole>
      }
    >
      {(catalog.overview?.catalogs ?? []).length === 0 ? (
        <Text size="sm" c="dimmed">
          Каталога ещё нет. Он нужен для подписей категорий и подсказок параметров в правилах.
          {years.length === 0 && ' Подключите агента с Revit — каталог снимает Revit на нём.'}
        </Text>
      ) : (
        <Table>
          <Table.Tbody>
            {catalog.overview!.catalogs.map((c) => (
              <Table.Tr key={c.revitYear}>
                <Table.Td fw={600} w={110}>Revit {c.revitYear}</Table.Td>
                <Table.Td>категорий {c.categories}, параметров {c.parameters}</Table.Td>
                <Table.Td>
                  {c.hasSharedParameterFile ? <Text size="sm" c="dimmed">с ФОП</Text> : (
                    <Tooltip label="У Revit на агенте не подключён файл общих параметров — ADSK-параметров в подсказках нет, опечатки не подсвечиваются" multiline maw={320}>
                      <Badge color="yellow" variant="light">без ФОП</Badge>
                    </Tooltip>
                  )}
                </Table.Td>
                <Table.Td c="dimmed">обновлён {formatDateTime(c.updatedAt)}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {runId && (
        <Anchor component={Link} to={`/history/runs/${runId}`} size="sm">
          Ход обновления — в истории
        </Anchor>
      )}
    </Panel>
  );
}

/** Загрузка файла профилей ParamChecker — сразу в группу (проект), чтобы не раскладывать профили потом по одному. */
function ImportModal({ groups, initialGroup, onClose }: { groups: string[]; initialGroup: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [group, setGroup] = useState(initialGroup);

  async function importFile(file: File | null) {
    if (!file) return;
    try {
      const result = await apiFetch<{ created: LoiProfileSummary[]; warnings: string[] }>('/api/loi/profiles/import',
        { method: 'POST', json: { json: await file.text(), group: group.trim() } });
      notifications.show({ color: 'green', message: `Загружено профилей: ${result.created.length} — ${result.created.map((p) => `«${profileDisplayName(p)}»`).join(', ')}` });
      for (const warning of result.warnings ?? [])
        notifications.show({ color: 'yellow', title: 'Перенеслось не всё', message: warning, autoClose: false });
      await queryClient.invalidateQueries({ queryKey: ['loi-profiles'] });
      onClose();
    } catch (error) {
      notifications.show({ color: 'red', title: 'Файл не загружен', message: (error as Error).message });
    }
  }

  return (
    <Modal opened onClose={onClose} title="Загрузить профили из ParamChecker">
      <Stack>
        <Autocomplete label="Группа" description="Обычно проект, например «Полковая». Пусто — без группы" data={groups} value={group} onChange={setGroup} />
        <Text size="sm" c="dimmed">
          Файл экспорта профилей ParamChecker или его profiles.json. Профили добавятся в группу; одноимённые получат номер — ничего не перезаписывается.
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t.common.cancel}</Button>
          <FileButton onChange={importFile} accept="application/json,.json">
            {(props) => <Button {...props}>Выбрать файл</Button>}
          </FileButton>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Переименовать группу целиком или слить с другой — например, когда проект переименовали. */
function RenameGroupModal({ from, groups, onClose }: { from: string; groups: string[]; onClose: () => void }) {
  const [to, setTo] = useState(from);
  const rename = useApiMutation<void>('PUT', () => '/api/loi/profile-groups', {
    invalidate: ['loi-profiles'],
    success: 'Группа переименована',
    json: () => ({ from, to: to.trim() }),
  });

  return (
    <Modal opened onClose={onClose} title={`Группа «${groupLabel(from)}»`}>
      <Stack>
        <Autocomplete label="Новое имя группы" description="Имя существующей группы — профили переедут в неё. Пусто — без группы"
          data={groups.filter((g) => g !== from)} value={to} onChange={setTo} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t.common.cancel}</Button>
          <Button loading={rename.isPending} onClick={() => rename.mutate(undefined, { onSuccess: onClose })}>Переименовать</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Ключ раздела списка: у Accordion не бывает пустого значения, а «без группы» — пустая строка. */
const sectionKey = (group: string) => group || '(без группы)';

export function LoiSettings() {
  const { can } = useAuth();
  const catalog = useLoiCatalog();
  const profiles = useApi<LoiProfileSummary[]>(['loi-profiles'], '/api/loi/profiles');
  const [editing, setEditing] = useState<Editing | null>(null);
  const [search, setSearch] = useState('');
  const [opened, setOpened] = useState<string[] | null>(null);
  const [importing, setImporting] = useState<string | null>(null);
  const [renaming, setRenaming] = useState<string | null>(null);
  const remove = useApiMutation<string>('DELETE', (id) => `/api/loi/profiles/${id}`, { invalidate: ['loi-profiles'], success: 'Профиль удалён' });

  const all = profiles.data ?? [];
  const groups = [...new Set(all.map((p) => p.group).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'ru'));
  const query = search.trim().toLowerCase();
  const sections = groupProfiles(query ? all.filter((p) => profileDisplayName(p).toLowerCase().includes(query)) : all);
  // Пока групп немного, все раскрыты; когда их много — свёрнуты, чтобы видеть список групп. Поиск раскрывает найденное.
  const expanded = query ? sections.map((g) => sectionKey(g.group)) : (opened ?? (sections.length <= 3 ? sections.map((g) => sectionKey(g.group)) : []));

  async function open(id: string) {
    try {
      const details = await apiFetch<LoiProfileDetails>(`/api/loi/profiles/${id}`);
      setEditing({ id, group: details.group, profile: normalizeProfile(details.profile) });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  /** Копия профиля — заготовка для того же раздела в другом проекте: открывается в редакторе, сохраняется отдельно. */
  async function copy(p: LoiProfileSummary) {
    try {
      const details = await apiFetch<LoiProfileDetails>(`/api/loi/profiles/${p.id}`);
      setEditing({ id: null, group: details.group, profile: { ...normalizeProfile(details.profile), Name: `${details.name} (копия)` } });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  async function download(profile: LoiProfileSummary) {
    try {
      await downloadFile(`/api/loi/profiles/${profile.id}/file`, `${profile.name}.json`);
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  return (
    <Stack>
      <Panel
        title={t.nav.loi}
        actions={
          <RequireRole role="Operator">
            <Group gap={6}>
              <Button size="xs" variant="default" onClick={() => setImporting('')}>Загрузить из ParamChecker</Button>
              <Button size="xs" onClick={() => setEditing({ id: null, group: '', profile: newProfile() })}>+ Новый профиль</Button>
            </Group>
          </RequireRole>
        }
      >
        <Text size="sm" c="dimmed" mb="sm">
          Профиль — правила ParamChecker: какие элементы отобрать и какие параметры у них должны быть заполнены. Профили раскладываются по группам —
          обычно по проектам; внутри группы имена уникальны, поэтому «ОВ» может быть в каждом проекте. Файлы профилей из плагина загружаются сюда как
          есть, а «Скачать» отдаёт файл, который плагин импортирует обратно.
        </Text>
        {profiles.data && all.length === 0 ? (
          <Empty>Профилей пока нет — создайте новый или загрузите файл из ParamChecker</Empty>
        ) : (
          <Stack gap="sm">
            {all.length > 5 && (
              <TextInput placeholder="Найти профиль по группе или имени" leftSection={<IconSearch size={14} />} value={search} maw={360}
                onChange={(e) => setSearch(e.currentTarget.value)} />
            )}
            {sections.length === 0 && <Empty>Ничего не найдено</Empty>}
            <Accordion multiple variant="separated" value={expanded} onChange={(value) => !query && setOpened(value)}>
              {sections.map(({ group, profiles: list }) => {
                const broken = list.filter((p) => p.problems.length > 0).length;
                return (
                  <Accordion.Item key={sectionKey(group)} value={sectionKey(group)}>
                    <Accordion.Control>
                      <Group gap="xs">
                        <Text fw={600} c={group ? undefined : 'dimmed'}>{groupLabel(group)}</Text>
                        <Badge variant="light" color="gray">{list.length}</Badge>
                        {broken > 0 && <Badge variant="light" color="red">не готово: {broken}</Badge>}
                      </Group>
                    </Accordion.Control>
                    <Accordion.Panel>
                      {can('Operator') && (
                        <Group gap={6} mb="xs">
                          <Button size="compact-xs" variant="default" onClick={() => setEditing({ id: null, group, profile: newProfile() })}>+ Профиль в группу</Button>
                          <Button size="compact-xs" variant="default" onClick={() => setImporting(group)}>Загрузить в группу</Button>
                          {group && <Button size="compact-xs" variant="subtle" onClick={() => setRenaming(group)}>Переименовать группу</Button>}
                        </Group>
                      )}
                      <Table highlightOnHover>
                        <Table.Tbody>
                          {list.map((p) => (
                            <Table.Tr key={p.id} onClick={() => open(p.id)} style={{ cursor: 'pointer' }}>
                              <Table.Td fw={600}>{p.name}</Table.Td>
                              <Table.Td>
                                {p.ruleCount} {pluralRu(p.ruleCount, ['правило', 'правила', 'правил'])}
                                {p.viewName && <Text span c="dimmed"> · вид «{p.viewName}»</Text>}
                              </Table.Td>
                              <Table.Td>
                                {p.problems.length === 0 ? <Badge color="green" variant="light">готов</Badge> : (
                                  <Tooltip label={p.problems.join('\n')} multiline maw={420}>
                                    <Badge color="red" variant="light">не готов</Badge>
                                  </Tooltip>
                                )}
                              </Table.Td>
                              <Table.Td c="dimmed">{p.updatedBy}, {formatDateTime(p.updatedAt)}</Table.Td>
                              <Table.Td>
                                <Group justify="flex-end" gap={6} wrap="nowrap" onClick={(e) => e.stopPropagation()}>
                                  <Button size="compact-xs" variant="subtle" onClick={() => download(p)}>Скачать</Button>
                                  <RequireRole role="Operator">
                                    <Button size="compact-xs" variant="subtle" onClick={() => copy(p)}>Копировать</Button>
                                    <ConfirmButton size="compact-xs" variant="subtle" question={`Удалить профиль «${profileDisplayName(p)}»?`} onConfirm={() => remove.mutate(p.id)}>
                                      {t.common.delete}
                                    </ConfirmButton>
                                  </RequireRole>
                                </Group>
                              </Table.Td>
                            </Table.Tr>
                          ))}
                        </Table.Tbody>
                      </Table>
                    </Accordion.Panel>
                  </Accordion.Item>
                );
              })}
            </Accordion>
          </Stack>
        )}
      </Panel>
      {editing && (
        <ProfileEditor key={editing.id ?? `new-${editing.profile.Name}`} id={editing.id} initial={editing.profile} initialGroup={editing.group} groups={groups}
          catalog={catalog} readOnly={!can('Operator')} onDone={() => setEditing(null)} />
      )}
      {importing !== null && <ImportModal groups={groups} initialGroup={importing} onClose={() => setImporting(null)} />}
      {renaming !== null && <RenameGroupModal from={renaming} groups={groups} onClose={() => setRenaming(null)} />}
      <CatalogPanel catalog={catalog} />
    </Stack>
  );
}
