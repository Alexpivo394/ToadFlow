import { useEffect, useState } from 'react';
import { ActionIcon, Alert, Button, Card, Group, MultiSelect, SimpleGrid, Stack, Text, Textarea, TextInput, Tooltip } from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { useApi, useApiMutation } from '../../api/queries';
import { useAuth } from '../../auth/AuthContext';
import { Panel } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, requestsKey, type Direction, type FamilyFacets } from './api';

interface DirectionDraft {
  name: string;
  sections: string[];
  disciplines: string[];
  coordinators: string[];
  hint: string;
}

const draftOf = (d: Direction): DirectionDraft => ({ name: d.name, sections: d.sections, disciplines: d.disciplines, coordinators: d.coordinators.map((c) => c.id), hint: d.hint });

/** Настройка направлений BIM (АР/КР, ОВ/ВК, ЭОМ/СС): разделы, координаторы, подсказка. Меняет администратор, видит координатор. */
export function DirectionsPanel() {
  const { can } = useAuth();
  const readOnly = !can('Admin');
  const saved = useApi<Direction[]>([requestsKey, 'directions'], '/api/families/directions');
  const uncovered = useApi<string[]>([requestsKey, 'directions', 'uncovered'], '/api/families/directions/uncovered');
  const facets = useApi<FamilyFacets>([familiesKey, 'facets', ''], '/api/families/facets');
  const disciplines = useApi<string[]>(['disciplines'], '/api/auth/disciplines');
  const people = useApi<{ id: string; name: string }[]>([requestsKey, 'assignees'], '/api/families/requests/assignees');
  const [draft, setDraft] = useState<DirectionDraft[] | null>(null);
  const save = useApiMutation<DirectionDraft[]>('PUT', () => '/api/families/directions', { invalidate: [requestsKey], success: t.insights.directionsSaved });

  useEffect(() => {
    if (saved.data && !draft) setDraft(saved.data.map(draftOf));
  }, [saved.data, draft]);

  const list = draft ?? [];
  const set = (index: number, value: Partial<DirectionDraft>) => setDraft(list.map((d, i) => (i === index ? { ...d, ...value } : d)));
  const sectionOptions = [...new Set([...(facets.data?.sections ?? []).map((s) => s.value), ...list.flatMap((d) => d.sections)])];
  const disciplineOptions = [...new Set([...(disciplines.data ?? []), ...list.flatMap((d) => d.disciplines)])];
  const peopleOptions = (people.data ?? []).map((p) => ({ value: p.id, label: p.name }));
  const invalid = list.some((d) => d.name.trim() === '');

  return (
    <Panel title={t.insights.directions}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">{t.insights.directionsHint}</Text>
        {(uncovered.data ?? []).length > 0 && list.length > 0 && (
          <Alert color="yellow">{t.insights.uncovered} {(uncovered.data ?? []).join(', ')}</Alert>
        )}
        {list.map((d, index) => (
          <Card key={index} withBorder radius="md" padding="sm">
            <Stack gap="xs">
              <Group align="flex-end" wrap="nowrap">
                <TextInput label={t.insights.name} placeholder="ОВ/ВК" value={d.name} disabled={readOnly} maxLength={60} style={{ flex: 1 }}
                  onChange={(e) => set(index, { name: e.currentTarget.value })} />
                {!readOnly && (
                  <Tooltip label={t.insights.removeDirection}>
                    <ActionIcon variant="subtle" color="red" size="lg" aria-label={t.insights.removeDirection} onClick={() => setDraft(list.filter((_, i) => i !== index))}>
                      <IconTrash size={18} />
                    </ActionIcon>
                  </Tooltip>
                )}
              </Group>
              <SimpleGrid cols={{ base: 1, sm: 3 }}>
                <MultiSelect label={t.insights.sections} data={sectionOptions} value={d.sections} disabled={readOnly} searchable
                  onChange={(value) => set(index, { sections: value })} />
                <MultiSelect label={t.insights.disciplines} description={t.insights.disciplinesHint} data={disciplineOptions} value={d.disciplines}
                  disabled={readOnly} searchable onChange={(value) => set(index, { disciplines: value })} />
                <MultiSelect label={t.insights.coordinators} data={peopleOptions} value={d.coordinators} disabled={readOnly} searchable
                  onChange={(value) => set(index, { coordinators: value })} />
              </SimpleGrid>
              <Textarea label={t.insights.hint} placeholder={t.insights.hintPlaceholder} autosize minRows={1} maxLength={1000} value={d.hint}
                disabled={readOnly} onChange={(e) => set(index, { hint: e.currentTarget.value })} />
            </Stack>
          </Card>
        ))}
        {!readOnly && (
          <Group>
            <Button variant="default" leftSection={<IconPlus size={16} />}
              onClick={() => setDraft([...list, { name: '', sections: [], disciplines: [], coordinators: [], hint: '' }])}>
              {t.insights.addDirection}
            </Button>
            <Button disabled={invalid} loading={save.isPending}
              onClick={() => save.mutate(list.map((d) => ({ ...d, name: d.name.trim() })), { onSuccess: () => setDraft(null) })}>
              {t.insights.saveDirections}
            </Button>
          </Group>
        )}
      </Stack>
    </Panel>
  );
}
