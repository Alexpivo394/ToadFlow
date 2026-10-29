import { Checkbox, Stack, Text, TextInput } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { ClashPartSummary, ClashScheduleSettings } from '../../api/types';

/**
 * Выгрузка геометрии для коллизий: какой 3D-вид выгружать, какие рабочие наборы не открывать и какие утверждённые части прогнать,
 * когда выгрузка закончится.
 */
export function ClashScheduleForm({ value, readOnly, onChange }: { value: ClashScheduleSettings; readOnly: boolean; onChange: (value: ClashScheduleSettings) => void }) {
  const parts = useApi<ClashPartSummary[]>(['clash-parts', 'list', 'all'], '/api/clash/parts');
  const selected = value.parts ?? [];
  const approved = (parts.data ?? []).filter((p) => p.approvedVersion != null);
  const notApproved = (parts.data ?? []).filter((p) => p.approvedVersion == null);

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
      <Stack gap={6}>
        <Text size="sm" fw={500}>Прогнать утверждённые части после выгрузки</Text>
        <Text size="xs" c="dimmed">
          Когда закончатся все задания запуска, каждая отмеченная часть прогоняется по утверждённой версии. Модели, которые не выгрузились, — «не проверены».
        </Text>
        {approved.length === 0 && <Text size="sm" c="dimmed">Утверждённых частей пока нет</Text>}
        {approved.map((p) => (
          <Checkbox key={p.id} disabled={readOnly} checked={selected.includes(p.id)}
            label={`${p.object ? `${p.object} · ` : ''}${p.name} (v${p.approvedVersion})`}
            onChange={(e) => onChange({ ...value, parts: e.currentTarget.checked ? [...selected, p.id] : selected.filter((id) => id !== p.id) })} />
        ))}
        {notApproved.map((p) => (
          <Text key={p.id} size="xs" c={selected.includes(p.id) ? 'red' : 'dimmed'}>{p.name} — не утверждена</Text>
        ))}
      </Stack>
    </Stack>
  );
}
