import { useEffect, useState } from 'react';
import { Button, Group, NumberInput, Select, Stack, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type FamiliesSettings, type LibraryAgent } from './api';
import { LibraryStatus } from './LibraryStatus';

/** Как проверяет сервер: \\сервер\папка или X:\… */
const rootPattern = /^(\\\\[^\\/]+\\[^\\/]+|[A-Za-z]:\\)/;
const anyAgent = 'any';

/** Параметры библиотеки: корень, папка проверки, интервал обхода, агент библиотеки. Меняет администратор, остальные смотрят. */
export function SettingsPage() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  const saved = useApi<FamiliesSettings>([familiesKey, 'settings'], '/api/families/settings');
  const agents = useApi<LibraryAgent[]>([familiesKey, 'agents'], '/api/families/agents');
  const [draft, setDraft] = useState<FamiliesSettings | null>(null);
  const save = useApiMutation<FamiliesSettings>('PUT', () => '/api/families/settings', { invalidate: [familiesKey], success: t.families.settingsSaved });

  useEffect(() => {
    if (saved.data && !draft) setDraft(saved.data);
  }, [saved.data, draft]);

  const value = draft ?? saved.data;
  const root = value?.root.trim() ?? '';
  const folder = value?.reviewFolder.trim() ?? '';
  const errors = {
    root: root.length > 0 && !rootPattern.test(root) ? 'Нужен путь UNC (\\\\сервер\\папка) или с буквой диска (Y:\\…)' : null,
    folder: folder.length === 0 || /[\\/:*?"<>|]/.test(folder) || folder.toLowerCase() === '.toadflow' ? 'Одно имя папки, без \\ и /' : null,
    interval: !value || !(value.scanIntervalMinutes >= 1 && value.scanIntervalMinutes <= 1440) ? 'От 1 до 1440 минут' : null,
  };
  const invalid = Object.values(errors).some(Boolean);
  const agentOptions = [
    { value: anyAgent, label: t.families.anyAgent },
    ...(agents.data ?? []).map((agent) => ({ value: agent.id, label: agent.online ? agent.machineName : `${agent.machineName} — ${t.families.agentOffline}` })),
  ];

  return (
    <Stack gap="md">
      <LibraryStatus />
      {value && (
        <Panel title="Библиотека">
          <Stack gap="sm" maw={640}>
            <TextInput label={t.families.root} description={t.families.rootHint} value={value.root} disabled={readOnly} error={errors.root}
              onChange={(e) => setDraft({ ...value, root: e.currentTarget.value })} />
            <TextInput label={t.families.reviewFolder} description={t.families.reviewFolderHint} value={value.reviewFolder} disabled={readOnly} error={errors.folder}
              onChange={(e) => setDraft({ ...value, reviewFolder: e.currentTarget.value })} />
            <NumberInput label={t.families.interval} w={240} min={1} max={1440} value={value.scanIntervalMinutes} disabled={readOnly} error={errors.interval}
              onChange={(v) => setDraft({ ...value, scanIntervalMinutes: Number(v) })} />
            <Select label={t.families.agent} description={t.families.agentHint} data={agentOptions} allowDeselect={false} disabled={readOnly}
              value={value.libraryAgentId ?? anyAgent} onChange={(v) => setDraft({ ...value, libraryAgentId: v === anyAgent || v === null ? null : v })} />
            {!readOnly && (
              <Group>
                <Button disabled={invalid} loading={save.isPending} onClick={() => save.mutate({ ...value, root, reviewFolder: folder })}>{t.common.save}</Button>
              </Group>
            )}
          </Stack>
        </Panel>
      )}
    </Stack>
  );
}
