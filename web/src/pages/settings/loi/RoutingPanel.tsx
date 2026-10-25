import { useEffect, useState } from 'react';
import { ActionIcon, Autocomplete, Button, Group, Select, Stack, Table, Text, Tooltip } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import { useApi, useApiMutation } from '../../../api/queries';
import type { LoiProfileSummary, LoiRouting, LoiRoutingView } from '../../../api/types';
import { Empty, Panel } from '../../../components/ui';
import { profileOptions } from '../../../lib/loi';

const skip = '__skip__';

/**
 * Какой профиль у модели какого раздела. Общая для всех расписаний «по разделу модели»: одно расписание на проект, а
 * раздел каждой модели — из справочника именования аналитики.
 */
export function RoutingPanel({ profiles, readOnly }: { profiles: LoiProfileSummary[]; readOnly: boolean }) {
  const saved = useApi<LoiRoutingView>(['loi-routing'], '/api/loi/routing');
  const [draft, setDraft] = useState<LoiRouting | null>(null);
  const save = useApiMutation<LoiRouting>('PUT', () => '/api/loi/routing', { invalidate: ['loi-routing'], success: 'Профили по разделам сохранены' });

  useEffect(() => {
    if (saved.data) setDraft({ disciplines: saved.data.disciplines, fallbackProfileId: saved.data.fallbackProfileId });
  }, [saved.data]);

  if (!draft || !saved.data) return null;
  const options = profileOptions(profiles);
  const used = new Set(draft.disciplines.map((d) => d.discipline.trim().toUpperCase()));
  const suggestions = saved.data.knownDisciplines.filter((d) => !used.has(d.toUpperCase()));
  const setRow = (index: number, patch: Partial<LoiRouting['disciplines'][number]>) =>
    setDraft({ ...draft, disciplines: draft.disciplines.map((d, i) => (i === index ? { ...d, ...patch } : d)) });

  return (
    <Panel title="Профили по разделам">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          Для расписаний с профилем «по разделу модели»: в одно расписание — весь проект, а профиль каждой модели выбирается по её разделу. Раздел
          берётся из имени модели справочником именования («Настройки → Аналитика LOI»), ручные поправки моделей главнее.
        </Text>
        {draft.disciplines.length === 0 ? (
          <Empty>Разделам ещё не назначены профили</Empty>
        ) : (
          <Table layout="fixed" verticalSpacing={6}>
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={200}>Раздел</Table.Th>
                <Table.Th>Профиль</Table.Th>
                {!readOnly && <Table.Th w={44} />}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {draft.disciplines.map((row, index) => (
                <Table.Tr key={index}>
                  <Table.Td>
                    <Autocomplete aria-label="Раздел" placeholder="ОВ" data={suggestions} value={row.discipline} disabled={readOnly}
                      onChange={(discipline) => setRow(index, { discipline })} />
                  </Table.Td>
                  <Table.Td>
                    <Select aria-label="Профиль" placeholder="Выберите профиль" data={options} value={row.profileId || null} allowDeselect={false}
                      searchable disabled={readOnly} onChange={(profileId) => setRow(index, { profileId: profileId ?? '' })} />
                  </Table.Td>
                  {!readOnly && (
                    <Table.Td>
                      <Tooltip label="Убрать">
                        <ActionIcon variant="subtle" color="red" aria-label="Убрать"
                          onClick={() => setDraft({ ...draft, disciplines: draft.disciplines.filter((_, i) => i !== index) })}>
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        {!readOnly && (
          <Group>
            <Button variant="default" size="xs" onClick={() => setDraft({ ...draft, disciplines: [...draft.disciplines, { discipline: '', profileId: '' }] })}>
              + Раздел
            </Button>
          </Group>
        )}
        <Select
          label="Остальные модели"
          description="Раздел не распознан или ему не назначен профиль"
          data={[{ value: skip, label: 'Пропускать — с пометкой в истории запуска' }, ...options]}
          value={draft.fallbackProfileId ?? skip}
          allowDeselect={false}
          disabled={readOnly}
          maw={420}
          onChange={(value) => setDraft({ ...draft, fallbackProfileId: !value || value === skip ? null : value })}
        />
        {!readOnly && (
          <Group>
            <Button onClick={() => save.mutate(draft)} loading={save.isPending}>Сохранить</Button>
          </Group>
        )}
      </Stack>
    </Panel>
  );
}
