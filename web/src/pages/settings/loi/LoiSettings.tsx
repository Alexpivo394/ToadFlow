import { useState } from 'react';
import { Link } from 'react-router';
import { Anchor, Badge, Button, FileButton, Group, Select, Stack, Table, Text, Tooltip } from '@mantine/core';
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
import { newProfile, normalizeProfile } from '../../../lib/loi';
import { ProfileEditor } from './ProfileEditor';
import { RoutingPanel } from './RoutingPanel';
import { useLoiCatalog, type LoiCatalog } from './useLoiCatalog';

type Editing = { id: string | null; profile: CheckProfile };

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

export function LoiSettings() {
  const { can } = useAuth();
  const queryClient = useQueryClient();
  const catalog = useLoiCatalog();
  const profiles = useApi<LoiProfileSummary[]>(['loi-profiles'], '/api/loi/profiles');
  const [editing, setEditing] = useState<Editing | null>(null);
  const remove = useApiMutation<string>('DELETE', (id) => `/api/loi/profiles/${id}`, { invalidate: ['loi-profiles'], success: 'Профиль удалён' });

  async function open(id: string) {
    try {
      const details = await apiFetch<LoiProfileDetails>(`/api/loi/profiles/${id}`);
      setEditing({ id, profile: normalizeProfile(details.profile) });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  async function importFile(file: File | null) {
    if (!file) return;
    try {
      const result = await apiFetch<{ created: LoiProfileSummary[]; warnings: string[] }>('/api/loi/profiles/import',
        { method: 'POST', json: { json: await file.text() } });
      notifications.show({ color: 'green', message: `Загружено профилей: ${result.created.length} — ${result.created.map((p) => `«${p.name}»`).join(', ')}` });
      for (const warning of result.warnings ?? [])
        notifications.show({ color: 'yellow', title: 'Перенеслось не всё', message: warning, autoClose: false });
      await queryClient.invalidateQueries({ queryKey: ['loi-profiles'] });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Файл не загружен', message: (error as Error).message });
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
              <FileButton onChange={importFile} accept="application/json,.json">
                {(props) => <Button size="xs" variant="default" {...props}>Загрузить из ParamChecker</Button>}
              </FileButton>
              <Button size="xs" onClick={() => setEditing({ id: null, profile: newProfile() })}>+ Новый профиль</Button>
            </Group>
          </RequireRole>
        }
      >
        <Text size="sm" c="dimmed" mb="sm">
          Профиль — правила ParamChecker: какие элементы отобрать и какие параметры у них должны быть заполнены. Файлы профилей из плагина загружаются
          сюда как есть, а «Скачать» отдаёт файл, который плагин импортирует обратно.
        </Text>
        {profiles.data && profiles.data.length === 0 ? (
          <Empty>Профилей пока нет — создайте новый или загрузите файл из ParamChecker</Empty>
        ) : (
          <Table highlightOnHover>
            <Table.Tbody>
              {profiles.data?.map((p) => (
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
                        <ConfirmButton size="compact-xs" variant="subtle" question={`Удалить профиль «${p.name}»?`} onConfirm={() => remove.mutate(p.id)}>
                          {t.common.delete}
                        </ConfirmButton>
                      </RequireRole>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Panel>
      {editing && (
        <ProfileEditor key={editing.id ?? 'new'} id={editing.id} initial={editing.profile} catalog={catalog} readOnly={!can('Operator')}
          onDone={() => setEditing(null)} />
      )}
      <RoutingPanel profiles={profiles.data ?? []} readOnly={!can('Operator')} />
      <CatalogPanel catalog={catalog} />
    </Stack>
  );
}
