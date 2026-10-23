import { ActionIcon, Checkbox, Group, NumberInput, Select, SimpleGrid, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { IconArrowBackUp } from '@tabler/icons-react';
import type { ExportOverrides, ExportSettings, NwcParameters } from '../api/types';

type Key = keyof ExportSettings;
type Field = { key: Key; label: string; kind: 'bool' | 'text' | 'number' | 'parameters'; hint?: string };

const groups: { title: string; fields: Field[] }[] = [
  {
    title: 'Открытие',
    fields: [
      { key: 'skipLinks', label: 'Не загружать связи', kind: 'bool', hint: 'Модель копируется, в копии связи выгружены — открытие быстрее' },
      { key: 'audit', label: 'Проверка при открытии (Audit)', kind: 'bool' },
      { key: 'worksetKeyword', label: 'Не открывать рабочие наборы, содержащие', kind: 'text' },
    ],
  },
  {
    title: 'RVT — перед сохранением',
    fields: [
      { key: 'deleteRevitLinks', label: 'Удалить связи RVT', kind: 'bool' },
      { key: 'deleteCad', label: 'Удалить CAD', kind: 'bool' },
      { key: 'deletePointClouds', label: 'Удалить облака точек', kind: 'bool' },
      { key: 'purge', label: 'Очистить неиспользуемое', kind: 'bool', hint: 'Доступно с Revit 2024' },
      { key: 'compact', label: 'Сжать файл', kind: 'bool' },
    ],
  },
  {
    title: 'NWC — основное',
    fields: [
      { key: 'nwcViewName', label: '3D-вид для NWC', kind: 'text' },
      { key: 'nwcSharedCoordinates', label: 'Общие координаты', kind: 'bool' },
      { key: 'nwcConvertElementProperties', label: 'Преобразовывать свойства элементов', kind: 'bool' },
      { key: 'nwcParameters', label: 'Параметры', kind: 'parameters' },
      { key: 'nwcDivideIntoLevels', label: 'Разделять по уровням', kind: 'bool' },
    ],
  },
  {
    title: 'NWC — что экспортировать',
    fields: [
      { key: 'nwcExportLinks', label: 'Связанные файлы', kind: 'bool' },
      { key: 'nwcConvertLinkedCad', label: 'Связанные CAD', kind: 'bool' },
      { key: 'nwcExportParts', label: 'Части', kind: 'bool' },
      { key: 'nwcRoomGeometry', label: 'Геометрию помещений', kind: 'bool' },
      { key: 'nwcRoomAsAttribute', label: 'Помещения как атрибут', kind: 'bool' },
      { key: 'nwcConvertLights', label: 'Источники света', kind: 'bool' },
    ],
  },
  {
    title: 'NWC — дополнительно',
    fields: [
      { key: 'nwcExportElementIds', label: 'ID элементов', kind: 'bool' },
      { key: 'nwcExportUrls', label: 'Ссылки (URL)', kind: 'bool' },
      { key: 'nwcFindMissingMaterials', label: 'Искать отсутствующие материалы', kind: 'bool' },
      { key: 'nwcFacetingFactor', label: 'Коэффициент фасетизации', kind: 'number' },
    ],
  },
];

const parameterOptions: { value: NwcParameters; label: string }[] = [
  { value: 'None', label: 'не выгружать' },
  { value: 'Elements', label: 'параметры элементов' },
  { value: 'All', label: 'все параметры' },
];

type Props =
  | { base: ExportSettings; overrides: ExportOverrides; onOverridesChange: (overrides: ExportOverrides) => void; readOnly?: boolean }
  | { value: ExportSettings; onChange: (value: ExportSettings) => void; readOnly?: boolean };

/**
 * Настройки выгрузки, сгруппированные как в ToadExporter. С пресетом (<c>base</c>) редактируются отличия от него:
 * изменённое поле подсвечено и сбрасывается кнопкой «как в пресете».
 */
export function ExportSettingsForm(props: Props) {
  const withBase = 'base' in props;
  const effective: ExportSettings = withBase ? { ...props.base, ...props.overrides } : props.value;

  function set<K extends Key>(key: K, value: ExportSettings[K]) {
    if (!withBase) return props.onChange({ ...props.value, [key]: value });
    const next = { ...props.overrides };
    if (props.base[key] === value) delete next[key];
    else next[key] = value;
    props.onOverridesChange(next);
  }

  function reset(key: Key) {
    if (!withBase) return;
    const next = { ...props.overrides };
    delete next[key];
    props.onOverridesChange(next);
  }

  return (
    <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
      {groups.map((group) => (
        <Stack key={group.title} gap={8}>
          <Text size="xs" fw={700} c="dimmed" tt="uppercase">
            {group.title}
          </Text>
          {group.fields.map((field) => {
            const changed = withBase && field.key in props.overrides;
            const value = effective[field.key];
            const control =
              field.kind === 'bool' ? (
                <Checkbox
                  label={field.label}
                  description={field.hint}
                  checked={value as boolean}
                  disabled={props.readOnly}
                  onChange={(e) => set(field.key, e.currentTarget.checked as never)}
                />
              ) : field.kind === 'text' ? (
                <TextInput label={field.label} value={value as string} disabled={props.readOnly} onChange={(e) => set(field.key, e.currentTarget.value as never)} />
              ) : field.kind === 'number' ? (
                <NumberInput
                  label={field.label}
                  value={value as number}
                  min={0.01}
                  step={0.5}
                  decimalScale={2}
                  disabled={props.readOnly}
                  onChange={(v) => set(field.key, (typeof v === 'number' ? v : Number(v) || 0) as never)}
                />
              ) : (
                <Select label={field.label} data={parameterOptions} value={value as string} disabled={props.readOnly} allowDeselect={false}
                  onChange={(v) => v && set(field.key, v as never)} />
              );

            return (
              <Group key={field.key} gap={6} wrap="nowrap" align="flex-end"
                style={changed ? { borderLeft: '3px solid var(--mantine-color-orange-5)', paddingLeft: 8 } : undefined}>
                <div style={{ flex: 1 }}>{control}</div>
                {changed && !props.readOnly && (
                  <Tooltip label="Как в пресете">
                    <ActionIcon variant="subtle" color="orange" onClick={() => reset(field.key)} aria-label="Как в пресете">
                      <IconArrowBackUp size={16} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
            );
          })}
        </Stack>
      ))}
    </SimpleGrid>
  );
}

export const defaultExportSettings: ExportSettings = {
  skipLinks: true, audit: false, worksetKeyword: '',
  deleteRevitLinks: true, deleteCad: true, deletePointClouds: false, purge: true, compact: true,
  nwcViewName: 'Navisworks', nwcSharedCoordinates: true, nwcConvertElementProperties: false, nwcParameters: 'All', nwcDivideIntoLevels: true,
  nwcExportLinks: false, nwcConvertLinkedCad: true, nwcExportParts: false, nwcRoomGeometry: true, nwcRoomAsAttribute: true, nwcConvertLights: false,
  nwcExportElementIds: true, nwcExportUrls: true, nwcFindMissingMaterials: true, nwcFacetingFactor: 1,
};
