import { Stack, Text, TextInput } from '@mantine/core';
import type { ClashScheduleSettings } from '../../api/types';

/** Выгрузка геометрии для коллизий: какой 3D-вид выгружать и какие рабочие наборы не открывать. */
export function ClashScheduleForm({ value, readOnly, onChange }: { value: ClashScheduleSettings; readOnly: boolean; onChange: (value: ClashScheduleSettings) => void }) {
  return (
    <Stack gap="sm" maw={560}>
      <Text size="sm" c="dimmed">
        Агент открывает модель без связей и выгружает геометрию и параметры элементов, видимых на 3D-виде. Скрытое на виде в проверку коллизий
        не попадает. Выгрузка хранится на сервере, папка результатов не нужна.
      </Text>
      <TextInput label="3D-вид" description="Как для NWC: обычно «Navisworks»" value={value.viewName} disabled={readOnly} required
        error={value.viewName.trim() ? null : 'Укажите имя 3D-вида'} onChange={(e) => onChange({ ...value, viewName: e.currentTarget.value })} />
      <TextInput label="Не открывать рабочие наборы со словом" description="Пусто — открываются все" value={value.worksetKeyword} disabled={readOnly}
        onChange={(e) => onChange({ ...value, worksetKeyword: e.currentTarget.value })} />
    </Stack>
  );
}
