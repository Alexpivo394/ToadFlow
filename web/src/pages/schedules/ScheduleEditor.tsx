import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { Alert, Button, Checkbox, Group, NumberInput, Select, SimpleGrid, Stack, Table, Tabs, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi, useApiMutation } from '../../api/queries';
import type {
  ExportScheduleSettings, LoiProfileSummary, LoiScheduleSettings, ModuleSettings, Preset, ScheduleChange, ScheduleRequest, ScheduleView, Selection, Source,
} from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ExportSettingsForm } from '../../components/ExportSettingsForm';
import { ConfirmButton, Empty, Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { LoiScheduleForm } from './LoiScheduleForm';
import { RecurrenceFields } from './RecurrenceFields';
import { duplicateNames, fromSelectionDto, setFormats, toSelectionDto, type SelectionState } from './selection';
import { SourceTree } from './SourceTree';

const moduleOptions = [
  { value: 'export', label: 'Выгрузка RVT / NWC' },
  { value: 'loi', label: 'Проверка LOI (заполнение параметров)' },
];

const defaultFolders: Record<string, string> = {
  export: '\\\\nas\\exports\\{расписание}\\{дата}',
  loi: '\\\\nas\\loi\\{расписание}\\{дата}',
};

const loiDefaults = (profileId: string): LoiScheduleSettings => ({
  profileId, viewName: null, worksetKeyword: null, skipLinks: true, problemsOnly: false, historyFile: '', analytics: true,
});

const isLoiSettings = (settings: ModuleSettings): settings is LoiScheduleSettings => 'profileId' in settings;

/** У проверки LOI формата нет: отмеченное уходит как «RVT» — так его понимают и сервер, и агенты прежних версий. */
function forModule(selection: Selection, moduleKind: string): Selection {
  if (moduleKind === 'export') return selection;
  return {
    models: selection.models.map((m) => ({ ...m, rvt: true, nwc: false })),
    folders: selection.folders.map((f) => ({ ...f, rvt: true, nwc: false })),
  };
}

function blank(presetId: string): ScheduleRequest {
  return {
    name: '',
    moduleKind: 'export',
    sourceId: '',
    outputFolderTemplate: defaultFolders.export,
    selection: { models: [], folders: [] },
    moduleSettings: { presetId, overrides: {} },
    recurrenceKind: 'Weekly',
    daysOfWeekMask: 0b0011111,
    timeOfDay: '20:00:00',
    intervalHours: null,
    onceAt: null,
    priority: 0,
    modelTimeoutMinutes: 30,
    maxRetries: 1,
    isEnabled: true,
  };
}

function fromView(view: ScheduleView): ScheduleRequest {
  const { name, moduleKind, sourceId, outputFolderTemplate, selection, moduleSettings, recurrenceKind, daysOfWeekMask, timeOfDay, intervalHours, onceAt, priority,
    modelTimeoutMinutes, maxRetries, isEnabled } = view;
  const settings: ModuleSettings = isLoiSettings(moduleSettings)
    ? { ...loiDefaults(''), ...moduleSettings }
    : { presetId: moduleSettings.presetId, overrides: moduleSettings.overrides ?? {} };
  return { name, moduleKind, sourceId, outputFolderTemplate, selection, moduleSettings: settings,
    recurrenceKind, daysOfWeekMask, timeOfDay, intervalHours, onceAt, priority, modelTimeoutMinutes, maxRetries, isEnabled };
}

export function ScheduleEditor({ schedule }: { schedule: ScheduleView | null }) {
  const { can } = useAuth();
  const readOnly = !can('Operator');
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const sources = useApi<Source[]>(['sources'], '/api/sources');
  const presets = useApi<Preset[]>(['presets'], '/api/export/presets');
  const profiles = useApi<LoiProfileSummary[]>(['loi-profiles'], '/api/loi/profiles');
  const changes = useApi<ScheduleChange[]>(['schedules', schedule?.id, 'changes'], schedule ? `/api/schedules/${schedule.id}/changes` : null);
  const defaultPreset = presets.data?.find((p) => p.isDefault)?.id ?? '';

  const [form, setForm] = useState<ScheduleRequest>(() => (schedule ? fromView(schedule) : blank(defaultPreset)));
  const [selection, setSelection] = useState<SelectionState>(() => fromSelectionDto(form.selection));
  const [includeNew, setIncludeNew] = useState(() => form.selection.folders.length > 0 || form.selection.models.length === 0);
  const [errors, setErrors] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const exportSettings = isLoiSettings(form.moduleSettings) ? null : form.moduleSettings;
  const loiSettings = isLoiSettings(form.moduleSettings) ? form.moduleSettings : null;

  useEffect(() => {
    if (!schedule && exportSettings && !exportSettings.presetId && defaultPreset)
      setForm((f) => ({ ...f, moduleSettings: { ...(f.moduleSettings as ExportScheduleSettings), presetId: defaultPreset } }));
  }, [defaultPreset, schedule, exportSettings]);

  const set = (patch: Partial<ScheduleRequest>) => setForm((f) => ({ ...f, ...patch }));
  const source = sources.data?.find((s) => s.id === form.sourceId);
  const preset = presets.data?.find((p) => p.id === exportSettings?.presetId);
  const duplicates = useMemo(() => duplicateNames(selection), [selection]);
  const selected = toSelectionDto(selection);
  const isExport = form.moduleKind === 'export';

  /** Другая задача — свои настройки; папка результатов меняется, только если её не трогали. */
  function setModule(moduleKind: string) {
    if (moduleKind === form.moduleKind) return;
    const ready = profiles.data?.find((p) => p.problems.length === 0) ?? profiles.data?.[0];
    set({
      moduleKind,
      outputFolderTemplate: form.outputFolderTemplate === defaultFolders[form.moduleKind] ? defaultFolders[moduleKind] : form.outputFolderTemplate,
      moduleSettings: moduleKind === 'loi' ? loiDefaults(ready?.id ?? '') : { presetId: defaultPreset, overrides: {} },
    });
  }

  const run = useApiMutation<void>('POST', () => `/api/schedules/${schedule!.id}/run`, { invalidate: ['runs'], success: 'Запуск создан — смотрите «Историю»' });
  const toggle = useApiMutation<boolean>('POST', () => `/api/schedules/${schedule!.id}/enabled`, {
    invalidate: ['schedules'],
    json: (isEnabled) => ({ isEnabled }),
  });
  const remove = useApiMutation<void>('DELETE', () => `/api/schedules/${schedule!.id}`, { invalidate: ['schedules'], success: 'Расписание удалено' });

  async function save() {
    setSaving(true);
    setErrors(null);
    try {
      const body = { ...form, selection: forModule(selected, form.moduleKind) };
      const saved = schedule
        ? await apiFetch<ScheduleView>(`/api/schedules/${schedule.id}`, { method: 'PUT', json: body })
        : await apiFetch<ScheduleView>('/api/schedules', { method: 'POST', json: body });
      for (const warning of saved.warnings) notifications.show({ color: 'yellow', title: 'Обратите внимание', message: warning, autoClose: 10_000 });
      notifications.show({ color: 'green', message: 'Расписание сохранено' });
      await queryClient.invalidateQueries({ queryKey: ['schedules'] });
      if (!schedule) navigate(`/schedules/${saved.id}`);
    } catch (error) {
      setErrors((error as Error).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Panel title={schedule ? `Расписание «${schedule.name}»` : 'Новое расписание'}>
      {errors && (
        <Alert color="red" mb="sm">
          {errors}
        </Alert>
      )}
      <Tabs defaultValue="main" keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="main">Настройка</Tabs.Tab>
          <Tabs.Tab value="models">Модели ({selected.models.length + selected.folders.length})</Tabs.Tab>
          <Tabs.Tab value="module">{isExport ? 'Параметры RVT / NWC' : 'Проверка LOI'}</Tabs.Tab>
          {schedule && <Tabs.Tab value="history">История изменений</Tabs.Tab>}
        </Tabs.List>

        <Tabs.Panel value="main">
          <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
            <Stack gap="sm">
              <TextInput label="Название" value={form.name} disabled={readOnly} onChange={(e) => set({ name: e.currentTarget.value })} required />
              <Select label="Задача" data={moduleOptions} value={form.moduleKind} allowDeselect={false} disabled={readOnly}
                onChange={(v) => v && setModule(v)} />
              <Select
                label="Источник"
                description="Версия Revit определяется по нему"
                data={(sources.data ?? []).map((s) => ({ value: s.id, label: `${s.name}${s.revitYear ? ` · Revit ${s.revitYear}` : ''}` }))}
                value={form.sourceId || null}
                disabled={readOnly}
                onChange={(v) => set({ sourceId: v ?? '' })}
              />
              <TextInput
                label="Папка результатов"
                description="Подстановки: {дата}, {время}, {расписание}"
                value={form.outputFolderTemplate}
                disabled={readOnly}
                onChange={(e) => set({ outputFolderTemplate: e.currentTarget.value })}
              />
            </Stack>
            <Stack gap="sm">
              <RecurrenceFields value={form} readOnly={readOnly} onChange={(v) => set(v)} />
              <Group grow>
                <Select
                  label="Приоритет"
                  data={[{ value: '1', label: 'Высокий' }, { value: '0', label: 'Обычный' }, { value: '-1', label: 'Низкий' }]}
                  value={String(form.priority)}
                  allowDeselect={false}
                  disabled={readOnly}
                  onChange={(v) => set({ priority: Number(v) })}
                />
                <NumberInput label="Таймаут на модель, мин" min={1} value={form.modelTimeoutMinutes} disabled={readOnly}
                  onChange={(v) => set({ modelTimeoutMinutes: Number(v) || 30 })} />
                <NumberInput label="Повторять упавшие" min={0} max={10} value={form.maxRetries} disabled={readOnly}
                  onChange={(v) => set({ maxRetries: Number(v) || 0 })} />
              </Group>
              {schedule && (
                <Text size="xs" c="dimmed">
                  Создал: {schedule.createdBy}, {formatDateTime(schedule.createdAt)} · Последнее изменение: {schedule.updatedBy}, {formatDateTime(schedule.updatedAt)}
                </Text>
              )}
            </Stack>
          </SimpleGrid>
        </Tabs.Panel>

        <Tabs.Panel value="models">
          <Stack gap="sm">
            <Checkbox label="Добавлять новые модели из отмеченных папок автоматически" checked={includeNew} disabled={readOnly}
              onChange={(e) => setIncludeNew(e.currentTarget.checked)} />
            {duplicates.length > 0 && <Alert color="yellow">Одинаковые имена моделей: {duplicates.join(', ')} — файлы перезапишут друг друга.</Alert>}
            {source ? (
              <SourceTree sourceId={source.id} rootPath={source.rootPath} selection={selection} includeNew={includeNew} readOnly={readOnly}
                formats={isExport} onChange={setSelection} />
            ) : (
              <Empty>Выберите источник на вкладке «Настройка»</Empty>
            )}
            {(selected.models.length > 0 || selected.folders.length > 0) && (
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Отмечено</Table.Th>
                    {isExport && <Table.Th>RVT</Table.Th>}
                    {isExport && <Table.Th>NWC</Table.Th>}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {selected.folders.map((f) => (
                    <Table.Tr key={f.folderPath}>
                      <Table.Td>📁 {f.folderPath || '(весь источник)'} <Text span size="xs" c="dimmed">— и новые модели</Text></Table.Td>
                      {isExport && <Table.Td><Checkbox size="xs" checked={f.rvt} disabled={readOnly} onChange={(e) => setSelection(setFormats(selection, f.folderPath, { rvt: e.currentTarget.checked, nwc: f.nwc }))} /></Table.Td>}
                      {isExport && <Table.Td><Checkbox size="xs" checked={f.nwc} disabled={readOnly} onChange={(e) => setSelection(setFormats(selection, f.folderPath, { rvt: f.rvt, nwc: e.currentTarget.checked }))} /></Table.Td>}
                    </Table.Tr>
                  ))}
                  {selected.models.map((m) => (
                    <Table.Tr key={m.modelPath}>
                      <Table.Td>{m.name} <Text span size="xs" c="dimmed">{m.modelPath}</Text></Table.Td>
                      {isExport && <Table.Td><Checkbox size="xs" checked={m.rvt} disabled={readOnly} onChange={(e) => setSelection(setFormats(selection, m.modelPath, { rvt: e.currentTarget.checked, nwc: m.nwc }))} /></Table.Td>}
                      {isExport && <Table.Td><Checkbox size="xs" checked={m.nwc} disabled={readOnly} onChange={(e) => setSelection(setFormats(selection, m.modelPath, { rvt: m.rvt, nwc: e.currentTarget.checked }))} /></Table.Td>}
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="module">
          {exportSettings && (
            <Stack gap="md">
              <Select
                label="Пресет"
                description="Поля, отличающиеся от пресета, подсвечены"
                data={(presets.data ?? []).map((p) => ({ value: p.id, label: p.isDefault ? `${p.name} (по умолчанию)` : p.name }))}
                value={exportSettings.presetId || null}
                allowDeselect={false}
                disabled={readOnly}
                onChange={(v) => set({ moduleSettings: { ...exportSettings, presetId: v ?? '' } })}
              />
              {preset && (
                <ExportSettingsForm base={preset.settings} overrides={exportSettings.overrides} readOnly={readOnly}
                  onOverridesChange={(overrides) => set({ moduleSettings: { ...exportSettings, overrides } })} />
              )}
            </Stack>
          )}
          {loiSettings && (
            <LoiScheduleForm value={loiSettings} profiles={profiles.data ?? []} readOnly={readOnly} onChange={(moduleSettings) => set({ moduleSettings })} />
          )}
        </Tabs.Panel>

        {schedule && (
          <Tabs.Panel value="history">
            {(changes.data ?? []).length === 0 ? (
              <Empty>Изменений нет</Empty>
            ) : (
              <Table>
                <Table.Tbody>
                  {changes.data!.map((c, i) => (
                    <Table.Tr key={i}>
                      <Table.Td c="dimmed" w={130}>{formatDateTime(c.at)}</Table.Td>
                      <Table.Td w={180}>{c.userName}</Table.Td>
                      <Table.Td>{c.summary}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Tabs.Panel>
        )}
      </Tabs>

      {!readOnly && (
        <Group mt="lg">
          <Button onClick={save} loading={saving}>
            Сохранить
          </Button>
          {schedule && (
            <>
              <Button variant="default" onClick={() => run.mutate()} loading={run.isPending}>
                Запустить сейчас
              </Button>
              <Button variant="default" onClick={() => toggle.mutate(!schedule.isEnabled)}>
                {schedule.isEnabled ? 'Выключить' : 'Включить'}
              </Button>
              <ConfirmButton variant="subtle" color="red" question={`Удалить расписание «${schedule.name}»? Запуски останутся в истории.`}
                onConfirm={() => remove.mutate(undefined, { onSuccess: () => navigate('/schedules') })}>
                Удалить
              </ConfirmButton>
            </>
          )}
        </Group>
      )}
    </Panel>
  );
}
