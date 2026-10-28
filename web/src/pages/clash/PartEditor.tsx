import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { Alert, Button, Group, List, Menu, Stack, Tabs, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import type { ClashPartView, ClashTemplateView, DraftView, ModelCatalog } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { askConfirm, PageTitle, Panel } from '../../components/ui';
import type { ClashConfig } from '../../lib/clashConfig';
import { DraftRunPanel } from './DraftRunPanel';
import { ExclusionsEditor, MatrixEditor } from './MatrixEditor';
import { PartModelsTab } from './PartModelsTab';
import { SetEditor } from './SetEditor';

/** Часть проекта: модели, черновик проверки (наборы, матрица, исключения), шаблоны и прогон черновика. */
export function PartEditor() {
  const { id } = useParams();
  const { can } = useAuth();
  const readOnly = !can('Operator');
  const queryClient = useQueryClient();
  const part = useApi<ClashPartView>(['clash-parts', id], `/api/clash/parts/${id}`);
  const draft = useApi<DraftView>(['clash-parts', id, 'draft'], `/api/clash/parts/${id}/draft`);
  const templates = useApi<ClashTemplateView[]>(['clash-templates'], '/api/clash/templates');
  const modelIds = (part.data?.models ?? []).map((m) => `modelId=${m.modelId}`).join('&');
  const catalog = useApi<ModelCatalog>(['clash-models', 'catalog', id, modelIds], part.data ? `/api/clash/catalog${modelIds ? `?${modelIds}` : ''}` : null);
  const [config, setConfig] = useState<ClashConfig | null>(null);
  const [problems, setProblems] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [templateName, setTemplateName] = useState('');

  useEffect(() => {
    if (draft.data) {
      setConfig(draft.data.configuration);
      setProblems(draft.data.problems);
    }
  }, [draft.data]);

  if (!part.data || !draft.data || !config) return null;
  const disciplines = draft.data.disciplines;
  const dirty = JSON.stringify(config) !== JSON.stringify(draft.data.configuration);

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['clash-parts'] });

  async function save() {
    setSaving(true);
    try {
      const saved = await apiFetch<DraftView>(`/api/clash/parts/${id}/draft`, { method: 'PUT', json: config });
      setProblems(saved.problems);
      notifications.show({ color: 'green', message: 'Черновик сохранён' });
      await refresh();
    } catch (error) {
      setProblems((error as Error).message.split('; '));
    } finally {
      setSaving(false);
    }
  }

  async function act(path: string, success: string, body?: unknown) {
    try {
      const view = await apiFetch<DraftView>(path, { method: 'POST', json: body });
      if (view?.configuration) {
        setConfig(view.configuration);
        setProblems(view.problems);
      }
      notifications.show({ color: 'green', message: success });
      await refresh();
      await queryClient.invalidateQueries({ queryKey: ['clash-templates'] });
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    }
  }

  return (
    <>
      <PageTitle>{part.data.name}</PageTitle>
      {problems.length > 0 && (
        <Alert color="red" mb="sm" title="Черновик нельзя прогнать">
          <List size="sm">{problems.map((p) => <List.Item key={p}>{p}</List.Item>)}</List>
        </Alert>
      )}
      <Panel
        actions={!readOnly && (
          <Group gap="xs">
            <Menu>
              <Menu.Target><Button size="xs" variant="default">Шаблоны</Button></Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>Применить к этой части</Menu.Label>
                {(templates.data ?? []).map((t) => (
                  <Menu.Item key={t.id} onClick={() => askConfirm(`Заменить черновик части шаблоном «${t.name}»?`,
                    () => act(`/api/clash/templates/${t.id}/apply/${id}`, `Применён шаблон «${t.name}»`), 'blue')}>
                    {t.name} <Text span size="xs" c="dimmed">({t.sets} наборов)</Text>
                  </Menu.Item>
                ))}
                {(templates.data ?? []).length === 0 && <Menu.Item disabled>Шаблонов нет</Menu.Item>}
                <Menu.Divider />
                <Stack gap={4} p="xs">
                  <TextInput size="xs" placeholder="Название шаблона" value={templateName} onChange={(e) => setTemplateName(e.currentTarget.value)} />
                  <Button size="xs" disabled={!templateName.trim() || dirty}
                    onClick={() => act('/api/clash/templates', `Шаблон «${templateName}» сохранён`, { name: templateName, partId: id })}>
                    Сохранить черновик как шаблон
                  </Button>
                </Stack>
              </Menu.Dropdown>
            </Menu>
            <Button size="xs" onClick={save} loading={saving}>Сохранить черновик</Button>
          </Group>
        )}
      >
        <Tabs defaultValue="models" keepMounted={false}>
          <Tabs.List mb="md">
            <Tabs.Tab value="models">Модели ({part.data.models.length})</Tabs.Tab>
            <Tabs.Tab value="sets">Наборы</Tabs.Tab>
            <Tabs.Tab value="matrix">Матрица</Tabs.Tab>
            <Tabs.Tab value="exclusions">Исключения</Tabs.Tab>
            <Tabs.Tab value="run">Черновик</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="models"><PartModelsTab part={part.data} readOnly={readOnly} /></Tabs.Panel>
          <Tabs.Panel value="sets">
            <SetEditor config={config} disciplines={disciplines} partId={id!} catalog={catalog.data} readOnly={readOnly} onChange={setConfig} />
          </Tabs.Panel>
          <Tabs.Panel value="matrix">
            <MatrixEditor config={config} disciplines={disciplines} readOnly={readOnly} onChange={setConfig}
              onFill={() => act(`/api/clash/parts/${id}/draft/matrix/fill`, 'Матрица заполнена')} />
          </Tabs.Panel>
          <Tabs.Panel value="exclusions"><ExclusionsEditor config={config} readOnly={readOnly} onChange={setConfig} /></Tabs.Panel>
          <Tabs.Panel value="run"><DraftRunPanel partId={id!} readOnly={readOnly} dirty={dirty} /></Tabs.Panel>
        </Tabs>
      </Panel>
    </>
  );
}
