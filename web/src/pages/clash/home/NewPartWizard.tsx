import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { Button, Checkbox, Group, Modal, Radio, Select, Stack, Stepper, Table, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../../api/queries';
import type { ClashModelView, ClashPartSummary, ClashPartView, ClashProjectView, ClashTemplateView, ProjectModelView } from '../../../api/types';
import { formatDateTime } from '../../../lib/format';
import type { ProjectKey } from './ProjectList';

/** Модель в части: Id и поправка раздела ('' — как в разборе имени). */
export interface PickedModel {
  modelId: string;
  disciplineOverride: string;
}

function Freshness({ model }: { model: ProjectModelView }) {
  if (!model.latest) return <Text size="sm" c="orange">нет выгрузки</Text>;
  return <Text size="sm" c={model.stale ? 'orange' : undefined}>{model.stale ? 'старая · ' : ''}{formatDateTime(model.latest.extractedAt)}</Text>;
}

/**
 * Выбор моделей части: модели проекта по разделам, отмеченные — в части. Раздел правится прямо в строке; модель не из
 * проекта добавляется поиском по всем моделям.
 */
export function ModelPicker({ project, part, picked, onChange }: {
  project: ProjectKey;
  part: string | null;
  picked: PickedModel[];
  onChange: (picked: PickedModel[]) => void;
}) {
  const [all, setAll] = useState(false);
  const models = useApi<ProjectModelView[]>(['clash-projects', 'models', project], `/api/clash/projects/models?object=${encodeURIComponent(project)}`);
  const everything = useApi<ClashModelView[]>(['clash-models'], '/api/clash/models');
  const chosen = new Map(picked.map((p) => [p.modelId, p]));
  const projectModels = models.data ?? [];
  const extra = (everything.data ?? []).filter((m) => chosen.has(m.id) && !projectModels.some((p) => p.id === m.id));
  const rows: ProjectModelView[] = [
    ...projectModels.filter((m) => all || m.part === part || chosen.has(m.id)),
    ...extra.map((m) => ({ id: m.id, name: m.name, part: null, discipline: null, latest: m.latest, snapshotCount: m.snapshotCount, stale: !m.latest })),
  ];
  const byDiscipline = new Map<string, ProjectModelView[]>();
  for (const m of rows) {
    const key = m.discipline ?? 'Раздел не определён';
    byDiscipline.set(key, [...(byDiscipline.get(key) ?? []), m]);
  }

  const toggle = (id: string, on: boolean) =>
    onChange(on ? [...picked, { modelId: id, disciplineOverride: '' }] : picked.filter((p) => p.modelId !== id));
  const override = (id: string, value: string) => onChange(picked.map((p) => (p.modelId === id ? { ...p, disciplineOverride: value } : p)));

  return (
    <Stack gap="xs">
      <Table verticalSpacing={4}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={30} />
            <Table.Th>Модель</Table.Th>
            <Table.Th w={150}>Раздел</Table.Th>
            <Table.Th>Последняя выгрузка</Table.Th>
            <Table.Th ta="right">Элементов</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {[...byDiscipline].map(([discipline, list]) => [
            <Table.Tr key={`d-${discipline}`} bg="var(--mantine-color-default-hover)">
              <Table.Td colSpan={5}><Text size="xs" fw={700}>{discipline}</Text></Table.Td>
            </Table.Tr>,
            ...list.map((m) => (
              <Table.Tr key={m.id}>
                <Table.Td><Checkbox aria-label={m.name} checked={chosen.has(m.id)} onChange={(e) => toggle(m.id, e.currentTarget.checked)} /></Table.Td>
                <Table.Td><Text size="sm">{m.name}</Text></Table.Td>
                <Table.Td>
                  <TextInput size="xs" placeholder={m.discipline ?? 'раздел'} disabled={!chosen.has(m.id)} aria-label={`Раздел ${m.name}`}
                    value={chosen.get(m.id)?.disciplineOverride ?? ''} onChange={(e) => override(m.id, e.currentTarget.value)} />
                </Table.Td>
                <Table.Td><Freshness model={m} /></Table.Td>
                <Table.Td ta="right">{m.latest ? m.latest.elements.toLocaleString('ru-RU') : '—'}</Table.Td>
              </Table.Tr>
            )),
          ])}
        </Table.Tbody>
      </Table>
      <Group gap="md">
        <Checkbox size="xs" label="Показать все модели проекта" checked={all} onChange={(e) => setAll(e.currentTarget.checked)} />
        <Select size="xs" w={320} searchable placeholder="Добавить модель не из этого проекта" value={null} aria-label="Добавить модель"
          data={(everything.data ?? []).filter((m) => !chosen.has(m.id)).map((m) => ({ value: m.id, label: m.name }))}
          onChange={(id) => id && toggle(id, true)} />
      </Group>
    </Stack>
  );
}

type StartKind = 'disciplines' | 'template' | 'part';

/** Новая часть — два шага: модели и с чего начать черновик. */
export function NewPartWizard({ project, onClose }: { project: ProjectKey; onClose: () => void }) {
  const navigate = useNavigate();
  const [step, setStep] = useState(0);
  const [object, setObject] = useState<ProjectKey>(project);
  const [part, setPart] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [picked, setPicked] = useState<PickedModel[]>([]);
  const [start, setStart] = useState<StartKind>('disciplines');
  const [templateId, setTemplateId] = useState<string | null>(null);
  const [sourcePart, setSourcePart] = useState<string | null>(null);

  const projects = useApi<ClashProjectView[]>(['clash-projects'], '/api/clash/projects');
  const models = useApi<ProjectModelView[]>(['clash-projects', 'models', object], `/api/clash/projects/models?object=${encodeURIComponent(object)}`);
  const templates = useApi<ClashTemplateView[]>(['clash-templates'], '/api/clash/templates');
  const parts = useApi<ClashPartSummary[]>(['clash-parts', 'list', object], `/api/clash/parts?object=${encodeURIComponent(object)}`);
  const create = useApiMutation<object, ClashPartView>('POST', () => '/api/clash/parts', { invalidate: ['clash-parts', 'clash-projects'] });

  const buildingParts = useMemo(
    () => [...new Set((models.data ?? []).map((m) => m.part).filter((p): p is string => !!p))].sort((a, b) => a.localeCompare(b, 'ru')),
    [models.data],
  );

  const choosePart = (value: string | null) => {
    setPart(value);
    if (!name.trim() || name === part) setName(value ?? '');
    setPicked((models.data ?? []).filter((m) => m.part === value).map((m) => ({ modelId: m.id, disciplineOverride: '' })));
  };

  const submit = () =>
    create.mutate(
      {
        name: name.trim(),
        object: object || null,
        part,
        models: picked.map((p) => ({ modelId: p.modelId, disciplineOverride: p.disciplineOverride.trim() || null })),
        start: start === 'template' ? { kind: 'template', templateId } : start === 'part' ? { kind: 'part', partId: sourcePart } : { kind: 'disciplines' },
      },
      {
        onSuccess: (created) => {
          onClose();
          if (created?.id) navigate(`/clash/parts/${created.id}`);
        },
      },
    );

  const ready = start === 'disciplines' || (start === 'template' && templateId) || (start === 'part' && sourcePart);

  return (
    <Modal opened onClose={onClose} title="Новая часть" size="xl">
      <Stepper active={step} size="sm" mb="md">
        <Stepper.Step label="Модели">
          <Stack gap="sm">
            <Group grow align="flex-end">
              <Select label="Проект" value={object} allowDeselect={false}
                data={(projects.data ?? []).map((p) => ({ value: p.object ?? '', label: p.name }))}
                onChange={(v) => { setObject(v ?? ''); setPart(null); setPicked([]); }} />
              <Select label="Часть здания" placeholder="из имён моделей" value={part} data={buildingParts} onChange={choosePart} />
              <TextInput label="Название" value={name} onChange={(e) => setName(e.currentTarget.value)} />
            </Group>
            <ModelPicker project={object} part={part} picked={picked} onChange={setPicked} />
            <Text size="xs" c="dimmed">Модели подобраны по имени. Раздел — из имени, его можно поменять в строке.</Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={onClose}>Отмена</Button>
              <Button disabled={!name.trim() || picked.length === 0} onClick={() => setStep(1)}>Далее</Button>
            </Group>
          </Stack>
        </Stepper.Step>
        <Stepper.Step label="С чего начать">
          <Stack gap="sm">
            <Radio.Group value={start} onChange={(v) => setStart(v as StartKind)}>
              <Stack gap="sm">
                <Radio value="disciplines" label="По разделам: группа на раздел, набор «всё», полная матрица 20 мм" />
                <Group gap="sm">
                  <Radio value="template" label="Из шаблона" />
                  <Select size="xs" w={260} label="Шаблон" disabled={start !== 'template'} value={templateId} onChange={setTemplateId}
                    data={(templates.data ?? []).map((t) => ({ value: t.id, label: t.name }))} />
                </Group>
                <Group gap="sm">
                  <Radio value="part" label="Скопировать из части" />
                  <Select size="xs" w={260} label="Часть" disabled={start !== 'part'} value={sourcePart} onChange={setSourcePart}
                    data={(parts.data ?? []).map((p) => ({ value: p.id, label: p.name }))} />
                </Group>
              </Stack>
            </Radio.Group>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setStep(0)}>Назад</Button>
              <Button disabled={!ready} loading={create.isPending} onClick={submit}>Создать</Button>
            </Group>
          </Stack>
        </Stepper.Step>
      </Stepper>
    </Modal>
  );
}
