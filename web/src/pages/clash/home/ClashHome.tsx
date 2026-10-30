import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { Group, Table, Tabs, Text } from '@mantine/core';
import { useApi, useApiMutation } from '../../../api/queries';
import type { ClashProjectView, ClashTemplateView } from '../../../api/types';
import { useAuth } from '../../../auth/AuthContext';
import { ConfirmButton, Empty, PageTitle } from '../../../components/ui';
import { t } from '../../../i18n/ru';
import { formatDateTime } from '../../../lib/format';
import { PartCards } from './PartCards';
import { ProjectList, projectKey, type ProjectKey, storedProject, storeProject } from './ProjectList';
import { UploadsTab } from './UploadsTab';

export type ClashTab = 'checks' | 'uploads' | 'templates';

const paths: Record<ClashTab, string> = { checks: '/clash/setup/parts', uploads: '/clash/setup/snapshots', templates: '/clash/setup/templates' };

function TemplatesTab() {
  const { can } = useAuth();
  const templates = useApi<ClashTemplateView[]>(['clash-templates'], '/api/clash/templates');
  const remove = useApiMutation<string>('DELETE', (id) => `/api/clash/templates/${id}`, { invalidate: ['clash-templates'], success: 'Шаблон удалён' });
  if (templates.data && templates.data.length === 0)
    return <Empty>Шаблонов пока нет — сохраните настройки части как шаблон в редакторе части</Empty>;
  return (
    <Table>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Шаблон</Table.Th>
          <Table.Th>Группы</Table.Th>
          <Table.Th ta="right">Наборов</Table.Th>
          <Table.Th>Изменён</Table.Th>
          <Table.Th />
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {(templates.data ?? []).map((tpl) => (
          <Table.Tr key={tpl.id}>
            <Table.Td><Text fw={500} size="sm">{tpl.name}</Text></Table.Td>
            <Table.Td>{tpl.groups.join(', ')}</Table.Td>
            <Table.Td ta="right">{tpl.sets}</Table.Td>
            <Table.Td>{formatDateTime(tpl.updatedAt)}</Table.Td>
            <Table.Td ta="right">
              {can('Operator') && (
                <ConfirmButton size="xs" variant="subtle" color="red" question={`Удалить шаблон «${tpl.name}»?`} onConfirm={() => remove.mutate(tpl.id)}>
                  Удалить
                </ConfirmButton>
              )}
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

/** «Настройка проверок»: части по проектам, выгрузки моделей по проектам и шаблоны. */
export function ClashHome({ tab }: { tab: ClashTab }) {
  const navigate = useNavigate();
  const projects = useApi<ClashProjectView[]>(['clash-projects'], '/api/clash/projects');
  const [selected, setSelected] = useState<ProjectKey | null>(storedProject);

  // Сохранённый проект мог исчезнуть — тогда первый в списке.
  useEffect(() => {
    const list = projects.data ?? [];
    if (list.length > 0 && (selected === null || !list.some((p) => projectKey(p) === selected))) setSelected(projectKey(list[0]));
  }, [projects.data, selected]);

  const select = (key: ProjectKey) => {
    setSelected(key);
    storeProject(key);
  };

  const list = projects.data ?? [];
  const body = (content: (project: ProjectKey) => React.ReactNode, count: (p: ClashProjectView) => number) =>
    list.length === 0 && projects.data ? (
      <Empty>Проектов ещё нет — выгрузите модели расписанием «Выгрузка геометрии для коллизий»</Empty>
    ) : (
      <Group align="flex-start" gap="lg" wrap="nowrap">
        <ProjectList projects={list} selected={selected} onSelect={select} count={count} />
        {selected !== null && content(selected)}
      </Group>
    );

  return (
    <>
      <PageTitle>{t.nav.clashSetup}</PageTitle>
      <Tabs value={tab} onChange={(v) => navigate(paths[(v as ClashTab) ?? 'checks'])} keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="checks">Части</Tabs.Tab>
          <Tabs.Tab value="uploads">Выгрузки</Tabs.Tab>
          <Tabs.Tab value="templates">Шаблоны</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="checks">{body((p) => <PartCards key={p} project={p} />, (p) => p.parts)}</Tabs.Panel>
        <Tabs.Panel value="uploads">{body((p) => <UploadsTab key={p} project={p} />, (p) => p.models)}</Tabs.Panel>
        <Tabs.Panel value="templates"><TemplatesTab /></Tabs.Panel>
      </Tabs>
    </>
  );
}
