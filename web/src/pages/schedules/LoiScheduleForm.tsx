import { Link } from 'react-router';
import { ActionIcon, Anchor, Autocomplete, Badge, Button, Checkbox, Group, Select, SimpleGrid, Stack, Table, Text, TextInput, Tooltip } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import type { LoiProfileSummary, LoiRoutingPreviewRow, LoiScheduleSettings, ModelProfile, ProfileVia } from '../../api/types';
import { profileOptions } from '../../lib/loi';

const byDiscipline = '__discipline__';

const viaLabels: Record<ProfileVia, { label: string; color: string }> = {
  Model: { label: 'исключение', color: 'grape' },
  Schedule: { label: 'профиль расписания', color: 'blue' },
  Discipline: { label: 'по разделу', color: 'teal' },
  Fallback: { label: 'для остальных', color: 'gray' },
  None: { label: 'пропускается', color: 'red' },
};

export interface ModelRef {
  name: string;
  modelPath: string;
}

interface Props {
  value: LoiScheduleSettings;
  profiles: LoiProfileSummary[];
  /** Сохранённое расписание — к отмеченным моделям в предпросмотре добавятся модели его последнего запуска. */
  scheduleId?: string;
  /** Модели, отмеченные в расписании явно. */
  models: ModelRef[];
  readOnly?: boolean;
  onChange: (value: LoiScheduleSettings) => void;
}

const modelName = (name: string) => name.split(/[\\/]/).pop()!.replace(/\.rvt$/i, '');

/** Своё значение расписания или «как в профиле» (null) — для вида и ключевого слова РН. */
function Override({ label, hint, profileValue, value, readOnly, onChange }: {
  label: string;
  hint: string;
  profileValue: string | null;
  value: string | null;
  readOnly?: boolean;
  onChange: (value: string | null) => void;
}) {
  return (
    <Stack gap={6}>
      <Checkbox label={`${label}: своё для расписания`} checked={value !== null} disabled={readOnly}
        onChange={(e) => onChange(e.currentTarget.checked ? profileValue ?? '' : null)} />
      {value === null ? (
        <Text size="sm" c="dimmed" lh="36px">
          {profileValue === null ? 'как в профиле каждой модели' : `как в профиле: ${profileValue ? `«${profileValue}»` : hint}`}
        </Text>
      ) : (
        <TextInput value={value} placeholder={hint} disabled={readOnly} onChange={(e) => onChange(e.currentTarget.value)} />
      )}
    </Stack>
  );
}

/** Какой профиль достанется каждой модели — с ещё не сохранёнными настройками. */
function Preview({ value, scheduleId, models }: { value: LoiScheduleSettings; scheduleId?: string; models: ModelRef[] }) {
  const preview = useQuery({
    queryKey: ['loi-routing', 'preview', scheduleId, value.profileId, value.modelProfiles, models],
    queryFn: () => apiFetch<LoiRoutingPreviewRow[]>('/api/loi/routing/preview', { method: 'POST', json: { settings: value, scheduleId, models } }),
    placeholderData: keepPreviousData,
  });
  const rows = preview.data ?? [];
  if (rows.length === 0) {
    return (
      <Text size="sm" c="dimmed">
        Модели появятся здесь, когда вы отметите их на вкладке «Модели». Модели из отмеченных папок станут известны после первого запуска.
      </Text>
    );
  }

  const skipped = rows.filter((r) => r.via === 'None').length;
  return (
    <Stack gap={6}>
      <Table layout="fixed" verticalSpacing={4} striped>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Модель</Table.Th>
            <Table.Th w={110}>Раздел</Table.Th>
            <Table.Th w="32%">Профиль</Table.Th>
            <Table.Th w={160}>Почему</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((r) => (
            <Table.Tr key={r.modelPath || r.name}>
              <Table.Td style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={r.modelPath}>{r.name}</Table.Td>
              <Table.Td c={r.discipline ? undefined : 'dimmed'}>{r.discipline ?? '—'}</Table.Td>
              <Table.Td c={r.profileName ? undefined : 'dimmed'}>{r.profileName ?? '—'}</Table.Td>
              <Table.Td><Badge variant="light" color={viaLabels[r.via].color}>{viaLabels[r.via].label}</Badge></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
      {skipped > 0 && (
        <Text size="xs" c="dimmed">
          Пропускаемые модели не проверяются: назначьте профиль их разделу в{' '}
          <Anchor component={Link} to="/settings/loi" size="xs">настройках LOI</Anchor> или добавьте исключение.
        </Text>
      )}
    </Stack>
  );
}

/** Что проверять и куда писать итоги: профиль LOI (один или по разделу модели), исключения и отличия от профиля. */
export function LoiScheduleForm({ value, profiles, scheduleId, models, readOnly, onChange }: Props) {
  const set = (patch: Partial<LoiScheduleSettings>) => onChange({ ...value, ...patch });
  const profile = profiles.find((p) => p.id === value.profileId);
  const options = profileOptions(profiles);
  const exceptions = value.modelProfiles;
  const setException = (index: number, patch: Partial<ModelProfile>) =>
    set({ modelProfiles: exceptions.map((m, i) => (i === index ? { ...m, ...patch } : m)) });
  const knownModels = [...new Set(models.map((m) => modelName(m.name)))];
  const broken = [profile, ...exceptions.map((m) => profiles.find((p) => p.id === m.profileId))].filter((p) => p && p.problems.length > 0);

  return (
    <Stack gap="lg">
      <Stack gap="xs">
        <Group align="flex-end" gap="sm">
          <Select
            label="Профиль проверки"
            data={[{ group: 'Для каждой модели', items: [{ value: byDiscipline, label: 'По разделу модели' }] }, { group: 'Один на всё расписание', items: options }]}
            value={value.profileId ?? byDiscipline}
            allowDeselect={false}
            disabled={readOnly}
            w={360}
            onChange={(id) => set({ profileId: !id || id === byDiscipline ? null : id })}
          />
          <Anchor component={Link} to="/settings/loi" size="sm" mb={8}>
            Профили и разделы
          </Anchor>
        </Group>
        {value.profileId === null && (
          <Text size="sm" c="dimmed">Профиль каждой модели — по её разделу, как назначено в настройках LOI. Исключения ниже главнее.</Text>
        )}
        {broken.map((p) => (
          <Text key={p!.id} size="sm" c="red">Профиль «{p!.name}» не готов к запуску: {p!.problems.join('; ')}</Text>
        ))}
      </Stack>

      <Stack gap="xs">
        <Text size="sm" fw={500}>Исключения</Text>
        {exceptions.length === 0 ? (
          <Text size="sm" c="dimmed">Нет — всем моделям профиль {value.profileId === null ? 'по разделу' : 'расписания'}.</Text>
        ) : (
          <Stack gap={8}>
            {exceptions.map((m, index) => (
              <Group key={index} gap="sm" wrap="nowrap">
                <Autocomplete aria-label="Модель" placeholder="Имя модели" data={knownModels} value={m.model} disabled={readOnly} style={{ flex: 3 }}
                  onChange={(model) => setException(index, { model: modelName(model) })} />
                <Select aria-label="Профиль" placeholder="Профиль" data={options} value={m.profileId || null} searchable allowDeselect={false}
                  disabled={readOnly} style={{ flex: 2 }} onChange={(profileId) => setException(index, { profileId: profileId ?? '' })} />
                {!readOnly && (
                  <Tooltip label="Убрать">
                    <ActionIcon variant="subtle" color="red" size="lg" aria-label="Убрать исключение"
                      onClick={() => set({ modelProfiles: exceptions.filter((_, i) => i !== index) })}>
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
            ))}
          </Stack>
        )}
        {!readOnly && (
          <Group>
            <Button variant="default" size="xs" onClick={() => set({ modelProfiles: [...exceptions, { model: '', profileId: '' }] })}>+ Исключение</Button>
          </Group>
        )}
      </Stack>

      <Stack gap="xs">
        <Text size="sm" fw={500}>Профили моделей</Text>
        <Preview value={value} scheduleId={scheduleId} models={models} />
      </Stack>

      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
        <Override label="Вид" hint="вся модель" profileValue={profile ? profile.viewName : null} value={value.viewName} readOnly={readOnly}
          onChange={(viewName) => set({ viewName })} />
        <Override label="Не открывать РН, содержащие" hint="открываются все" profileValue={profile ? profile.worksetKeyword : null} value={value.worksetKeyword}
          readOnly={readOnly} onChange={(worksetKeyword) => set({ worksetKeyword })} />
      </SimpleGrid>

      <Stack gap="sm">
        <Checkbox label="Не загружать связи" description="Модель копируется, в копии связи выгружены — открытие быстрее" checked={value.skipLinks}
          disabled={readOnly} onChange={(e) => set({ skipLinks: e.currentTarget.checked })} />
        <Checkbox label="В листах правил только элементы с пропусками" description="Итоги — по всем элементам; отчёты по большим моделям в разы меньше"
          checked={value.problemsOnly} disabled={readOnly} onChange={(e) => set({ problemsOnly: e.currentTarget.checked })} />
        <Checkbox label="Записывать итоги в аналитику ToadFlow" description="Страница «Аналитика LOI»: история по объектам, разделам и моделям"
          checked={value.analytics} disabled={readOnly} onChange={(e) => set({ analytics: e.currentTarget.checked })} />
      </Stack>

      <TextInput
        label="База для дашбордов"
        description="Книга Excel, куда каждая модель дописывает итоги (как в ParamChecker) — для Power BI. Пусто — не ведётся. Подстановки: {дата}, {время}, {расписание}"
        placeholder="\\nas\loi\LOI_База.xlsx"
        value={value.historyFile}
        disabled={readOnly}
        onChange={(e) => set({ historyFile: e.currentTarget.value })}
      />
    </Stack>
  );
}
