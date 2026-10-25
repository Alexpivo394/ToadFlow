import { Link } from 'react-router';
import { Anchor, Checkbox, Group, Select, SimpleGrid, Stack, Text, TextInput } from '@mantine/core';
import type { LoiProfileSummary, LoiScheduleSettings } from '../../api/types';

interface Props {
  value: LoiScheduleSettings;
  profiles: LoiProfileSummary[];
  readOnly?: boolean;
  onChange: (value: LoiScheduleSettings) => void;
}

/** Своё значение расписания или «как в профиле» (null) — для вида и ключевого слова РН. */
function Override({ label, hint, profileValue, value, readOnly, onChange }: {
  label: string;
  hint: string;
  profileValue: string;
  value: string | null;
  readOnly?: boolean;
  onChange: (value: string | null) => void;
}) {
  return (
    <Stack gap={4}>
      <Checkbox label={`${label}: своё для расписания`} checked={value !== null} disabled={readOnly}
        onChange={(e) => onChange(e.currentTarget.checked ? profileValue : null)} />
      {value === null ? (
        <Text size="sm" c="dimmed">как в профиле: {profileValue ? `«${profileValue}»` : hint}</Text>
      ) : (
        <TextInput value={value} placeholder={hint} disabled={readOnly} onChange={(e) => onChange(e.currentTarget.value)} />
      )}
    </Stack>
  );
}

/** Что проверять и куда писать итоги: профиль LOI и отличия от него. */
export function LoiScheduleForm({ value, profiles, readOnly, onChange }: Props) {
  const set = (patch: Partial<LoiScheduleSettings>) => onChange({ ...value, ...patch });
  const profile = profiles.find((p) => p.id === value.profileId);

  return (
    <Stack gap="md">
      <Group align="flex-end" gap="sm">
        <Select
          label="Профиль проверки"
          placeholder={profiles.length ? 'Выберите профиль' : 'Профилей нет'}
          data={profiles.map((p) => ({ value: p.id, label: p.problems.length ? `${p.name} (не готов)` : p.name }))}
          value={value.profileId || null}
          allowDeselect={false}
          disabled={readOnly}
          w={360}
          onChange={(profileId) => set({ profileId: profileId ?? '' })}
        />
        <Anchor component={Link} to="/settings/loi" size="sm" mb={8}>
          Профили LOI
        </Anchor>
      </Group>
      {profile && profile.problems.length > 0 && (
        <Text size="sm" c="red">Профиль не готов к запуску: {profile.problems.join('; ')}</Text>
      )}

      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
        <Override label="Вид" hint="вся модель" profileValue={profile?.viewName ?? ''} value={value.viewName} readOnly={readOnly}
          onChange={(viewName) => set({ viewName })} />
        <Override label="Не открывать РН, содержащие" hint="открываются все" profileValue={profile?.worksetKeyword ?? ''} value={value.worksetKeyword}
          readOnly={readOnly} onChange={(worksetKeyword) => set({ worksetKeyword })} />
      </SimpleGrid>

      <Stack gap={8}>
        <Checkbox label="Не загружать связи" description="Модель копируется, в копии связи выгружены — открытие быстрее" checked={value.skipLinks}
          disabled={readOnly} onChange={(e) => set({ skipLinks: e.currentTarget.checked })} />
        <Checkbox label="В листах правил только элементы с пропусками" description="Итоги — по всем элементам; отчёты по большим моделям в разы меньше"
          checked={value.problemsOnly} disabled={readOnly} onChange={(e) => set({ problemsOnly: e.currentTarget.checked })} />
      </Stack>

      <Checkbox label="Записывать итоги в аналитику ToadFlow" description="Страница «Аналитика LOI»: история по объектам, разделам и моделям"
        checked={value.analytics} disabled={readOnly} onChange={(e) => set({ analytics: e.currentTarget.checked })} />

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
