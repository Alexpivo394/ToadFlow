import { useState } from 'react';
import { Button, Group, Modal, NumberInput, SegmentedControl, Stack, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { Source, SourceKind } from '../../api/types';
import { RequireRole } from '../../auth/RequireRole';
import { DataTable } from '../../components/DataTable';
import { ConfirmButton, Panel, YearBadge } from '../../components/ui';
import { t } from '../../i18n/ru';

type Draft = Omit<Source, 'id'> & { id?: string };

function SourceModal({ source, onClose }: { source: Draft; onClose: () => void }) {
  const [draft, setDraft] = useState<Draft>(source);
  const set = (patch: Partial<Draft>) => setDraft((d) => ({ ...d, ...patch }));
  const save = useApiMutation<Draft>(source.id ? 'PUT' : 'POST', (d) => (d.id ? `/api/sources/${d.id}` : '/api/sources'), {
    invalidate: ['sources'],
    success: 'Источник сохранён',
  });

  return (
    <Modal opened onClose={onClose} title={source.id ? 'Источник' : 'Новый источник'}>
      <Stack>
        <TextInput label="Название" value={draft.name} onChange={(e) => set({ name: e.currentTarget.value })} />
        <SegmentedControl value={draft.kind} onChange={(v) => set({ kind: v as SourceKind })}
          data={[{ value: 'RevitServer', label: 'Revit Server' }, { value: 'Folder', label: 'Сетевая папка' }]} />
        {draft.kind === 'RevitServer' ? (
          <>
            <TextInput label="Имя или адрес Revit Server" placeholder="srv-rs22" value={draft.host} onChange={(e) => set({ host: e.currentTarget.value })} />
            <NumberInput label="Версия Revit" description="Revit Server обслуживает одну версию" min={2020} max={2027} value={draft.revitYear ?? 2024}
              onChange={(v) => set({ revitYear: Number(v) || null })} />
            <TextInput label="Корневая папка на сервере" description="Пусто — весь сервер" value={draft.rootPath} onChange={(e) => set({ rootPath: e.currentTarget.value })} />
          </>
        ) : (
          <TextInput label="Папка" placeholder={'\\\\nas\\projects'} description="Версия Revit берётся из каждого файла" value={draft.host}
            onChange={(e) => set({ host: e.currentTarget.value })} />
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t.common.cancel}</Button>
          <Button loading={save.isPending} onClick={() => save.mutate({ ...draft, revitYear: draft.kind === 'RevitServer' ? (draft.revitYear ?? 2024) : null }, { onSuccess: onClose })}>
            {t.common.save}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export function SourcesPage() {
  const sources = useApi<Source[]>(['sources'], '/api/sources');
  const [editing, setEditing] = useState<Draft | null>(null);
  const remove = useApiMutation<string>('DELETE', (id) => `/api/sources/${id}`, { invalidate: ['sources'], success: 'Источник удалён' });

  return (
    <Panel
      title="Источники"
      actions={
        <RequireRole role="Admin">
          <Button size="xs" onClick={() => setEditing({ name: '', kind: 'RevitServer', host: '', revitYear: 2024, rootPath: '' })}>+ Добавить</Button>
        </RequireRole>
      }
    >
      <DataTable rows={sources.data ?? []} rowKey={(s) => s.id} search={(s) => `${s.name} ${s.host} ${s.rootPath}`}
        empty="Источников нет — добавьте Revit Server или сетевую папку"
        columns={[
          { key: 'name', title: 'Название', sort: (s) => s.name, render: (s) => <Text fw={600} size="sm">{s.name}</Text> },
          {
            key: 'where', title: 'Где', sort: (s) => `${s.kind} ${s.host}`,
            render: (s) => (s.kind === 'RevitServer' ? `Revit Server ${s.host}${s.rootPath ? ` / ${s.rootPath}` : ''}` : `Папка ${s.host}`),
            filter: { options: [{ value: 'RevitServer', label: 'Revit Server' }, { value: 'Folder', label: 'Папка' }], match: (s, values) => values.includes(s.kind) },
          },
          {
            key: 'year', title: 'Revit', sort: (s) => s.revitYear,
            render: (s) => (s.revitYear ? <YearBadge year={s.revitYear} /> : <Text size="sm" c="dimmed">версия по каждому файлу</Text>),
          },
          {
            key: 'actions', title: '',
            render: (s) => (
              <RequireRole role="Admin">
                <Group gap={4} justify="flex-end">
                  <Button size="compact-xs" variant="default" onClick={() => setEditing(s)}>{t.common.edit}</Button>
                  <ConfirmButton size="compact-xs" variant="subtle" question={`Удалить источник «${s.name}»?`} onConfirm={() => remove.mutate(s.id)}>
                    {t.common.delete}
                  </ConfirmButton>
                </Group>
              </RequireRole>
            ),
          },
        ]} />
      <Text size="xs" c="dimmed" mt="sm">
        Дерево моделей для расписаний строит агент: сервер на Linux сетевые папки Windows не видит.
      </Text>
      {editing && <SourceModal source={editing} onClose={() => setEditing(null)} />}
    </Panel>
  );
}
