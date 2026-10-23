import { useState } from 'react';
import { Badge, Button, Checkbox, Group, Stack, Table, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { ExportSettings, Preset } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { RequireRole } from '../../auth/RequireRole';
import { defaultExportSettings, ExportSettingsForm } from '../../components/ExportSettingsForm';
import { ConfirmButton, Panel } from '../../components/ui';
import { t } from '../../i18n/ru';

type Draft = { id?: string; name: string; isDefault: boolean; settings: ExportSettings };

function PresetEditor({ preset, onDone }: { preset: Draft; onDone: () => void }) {
  const { can } = useAuth();
  const [draft, setDraft] = useState<Draft>(preset);
  const save = useApiMutation<Draft>(preset.id ? 'PUT' : 'POST', (d) => (d.id ? `/api/export/presets/${d.id}` : '/api/export/presets'), {
    invalidate: ['presets'],
    success: 'Пресет сохранён',
  });
  const readOnly = !can('Operator');

  return (
    <Panel title={preset.id ? `Пресет «${preset.name}»` : 'Новый пресет'}>
      <Stack>
        <Group align="flex-end">
          <TextInput label="Название" value={draft.name} disabled={readOnly} onChange={(e) => setDraft({ ...draft, name: e.currentTarget.value })} w={300} />
          <Checkbox label="По умолчанию" checked={draft.isDefault} disabled={readOnly || preset.isDefault}
            onChange={(e) => setDraft({ ...draft, isDefault: e.currentTarget.checked })} />
        </Group>
        <ExportSettingsForm value={draft.settings} readOnly={readOnly} onChange={(settings) => setDraft({ ...draft, settings })} />
        {!readOnly && (
          <Group>
            <Button loading={save.isPending} onClick={() => save.mutate(draft, { onSuccess: onDone })}>{t.common.save}</Button>
            <Button variant="default" onClick={onDone}>{t.common.cancel}</Button>
          </Group>
        )}
      </Stack>
    </Panel>
  );
}

export function PresetsSettings() {
  const presets = useApi<Preset[]>(['presets'], '/api/export/presets');
  const [editing, setEditing] = useState<Draft | null>(null);
  const remove = useApiMutation<string>('DELETE', (id) => `/api/export/presets/${id}`, { invalidate: ['presets'], success: 'Пресет удалён' });

  return (
    <Stack>
      <Panel
        title="Пресеты выгрузки"
        actions={
          <RequireRole role="Operator">
            <Button size="xs" onClick={() => setEditing({ name: '', isDefault: false, settings: defaultExportSettings })}>+ Новый пресет</Button>
          </RequireRole>
        }
      >
        <Table>
          <Table.Tbody>
            {presets.data?.map((p) => (
              <Table.Tr key={p.id} onClick={() => setEditing(p)} style={{ cursor: 'pointer' }}>
                <Table.Td fw={600}>
                  {p.name} {p.isDefault && <Badge variant="light">по умолчанию</Badge>}
                </Table.Td>
                <Table.Td c="dimmed">
                  {[p.settings.skipLinks && 'без связей', p.settings.purge && 'purge', p.settings.compact && 'сжатие', `NWC по виду «${p.settings.nwcViewName}»`]
                    .filter(Boolean).join(', ')}
                </Table.Td>
                <Table.Td>
                  <RequireRole role="Operator">
                    {!p.isDefault && (
                      <Group justify="flex-end" onClick={(e) => e.stopPropagation()}>
                        <ConfirmButton size="compact-xs" variant="subtle" question={`Удалить пресет «${p.name}»?`} onConfirm={() => remove.mutate(p.id)}>
                          {t.common.delete}
                        </ConfirmButton>
                      </Group>
                    )}
                  </RequireRole>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Panel>
      {editing && <PresetEditor key={editing.id ?? 'new'} preset={editing} onDone={() => setEditing(null)} />}
    </Stack>
  );
}
