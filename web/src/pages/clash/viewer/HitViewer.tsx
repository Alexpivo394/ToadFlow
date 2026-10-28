import { lazy, Suspense, useEffect, useState } from 'react';
import { Badge, Box as Area, Button, Center, Checkbox, Group, Loader, Modal, RangeSlider, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetchBinary } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { DraftHitView, ViewerSceneView } from '../../../api/types';
import { type Box, defaultSectionBox, type Isolation, parseMeshes } from '../../../lib/viewer';
import { ElementPanel } from './ElementPanel';

// three.js тяжёлый: грузится отдельным чанком, только когда открыт просмотрщик.
const ClashViewer = lazy(() => import('./ClashViewer'));

const Radii = ['3000', '5000', '10000', '20000'];
const axes = ['X', 'Y', 'Z'] as const;

function plural(n: number, one: string, few: string, many: string) {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}

/**
 * Окно коллизии: 3D-сцена вокруг коллизии (A и B, окружение в радиусе), изоляция, секущий куб и свойства элементов.
 * Ему дают прогон и строки коллизий — черновик или (позже) отчёт; «пред.» и «след.» листают переданные строки.
 */
export function HitViewer({ runId, hits, index, onIndex, onClose }: {
  runId: string;
  hits: DraftHitView[];
  index: number;
  onIndex: (index: number) => void;
  onClose: () => void;
}) {
  const hit = hits[index];
  const [radius, setRadius] = useState('5000');
  const [isolation, setIsolation] = useState<Isolation>('all');
  const [sectionOn, setSectionOn] = useState(false);
  const [section, setSection] = useState<Box | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const queryClient = useQueryClient();

  const base = `/api/clash/runs/${runId}/hits/${hit.number}`;
  const scene = useApi<ViewerSceneView>(['clash-viewer', runId, hit.number, radius, 'scene'], `${base}/scene?radius=${radius}`);
  const meshes = useQuery({
    queryKey: ['clash-viewer', runId, hit.number, radius, 'meshes'],
    queryFn: async () => parseMeshes(await apiFetchBinary(`${base}/meshes?radius=${radius}`)),
    enabled: !!scene.data,
  });

  // Другая коллизия — новый секущий куб вокруг её пары и сброс выбора.
  useEffect(() => {
    setSelected(null);
    setSection(scene.data ? defaultSectionBox(scene.data) : null);
  }, [scene.data]);

  const context = scene.data?.elements.filter((e) => e.role === 'context') ?? [];
  const withMesh = context.filter((e) => e.mesh).length;
  const limit = scene.data?.radiusMm ?? Number(radius);

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
    onClose();
  };

  return (
    <Modal opened onClose={onClose} fullScreen title={
      <Group gap="sm">
        <Text fw={600}>Коллизия № {hit.number}</Text>
        <Text c="dimmed">{hit.disciplineA} × {hit.disciplineB} · глубина {Math.round(hit.depthMm)} мм</Text>
      </Group>
    }>
      <Stack gap="sm" h="calc(100vh - 90px)">
        <Group gap="md" align="flex-end" wrap="wrap">
          <Button size="xs" variant="default" disabled={index === 0} onClick={() => onIndex(index - 1)}>← пред.</Button>
          <Button size="xs" variant="default" disabled={index >= hits.length - 1} onClick={() => onIndex(index + 1)}>след. →</Button>
          <SegmentedControl size="xs" value={isolation} onChange={(v) => setIsolation(v as Isolation)}
            data={[{ value: 'all', label: 'Всё' }, { value: 'pair', label: 'Только A и B' }, { value: 'selected', label: 'Только выбранный' }]} />
          <Select size="xs" w={150} allowDeselect={false} value={radius} onChange={(v) => v && setRadius(v)} aria-label="Радиус окружения"
            data={Radii.map((r) => ({ value: r, label: `окружение ${Number(r) / 1000} м` }))} />
          <Checkbox size="xs" label="Секущий куб" checked={sectionOn} onChange={(e) => setSectionOn(e.currentTarget.checked)} />
          {scene.data && (
            <Text size="xs" c="dimmed">
              Окружение: {context.length} {plural(context.length, 'элемент', 'элемента', 'элементов')}, {withMesh} с сеткой
            </Text>
          )}
          {scene.data?.truncated && <Badge color="orange" variant="light">окружение урезано</Badge>}
        </Group>

        {sectionOn && section && (
          <Group gap="lg" grow>
            {axes.map((axis, i) => (
              <Stack key={axis} gap={2}>
                <Text size="xs" c="dimmed">{axis}, мм от точки коллизии</Text>
                <RangeSlider size="xs" min={-limit} max={limit} step={50} minRange={50} label={null}
                  value={[Math.max(-limit, section.min[i]), Math.min(limit, section.max[i])]}
                  onChange={([lo, hi]) => {
                    const min = [...section.min] as Box['min'];
                    const max = [...section.max] as Box['max'];
                    min[i] = lo;
                    max[i] = hi;
                    setSection({ min, max });
                  }} />
              </Stack>
            ))}
          </Group>
        )}

        <Group gap="md" align="stretch" wrap="nowrap" style={{ flex: 1, minHeight: 0 }}>
          <Area style={{ flex: 1, minWidth: 0, border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
            {scene.error ? (
              <Center h="100%">
                <Stack align="center" gap="sm">
                  <Text c="red">{scene.error.message}</Text>
                  <Button size="xs" onClick={refresh}>Обновить список</Button>
                </Stack>
              </Center>
            ) : scene.data ? (
              <Suspense fallback={<Center h="100%"><Loader /></Center>}>
                <ClashViewer scene={scene.data} meshes={meshes.data} isolation={isolation} sectionBox={sectionOn ? section : null}
                  selected={selected} onPick={setSelected} />
              </Suspense>
            ) : (
              <Center h="100%"><Loader /></Center>
            )}
          </Area>
          <Area w={340} style={{ flexShrink: 0 }}>
            {meshes.error && <Text size="xs" c="red">{meshes.error.message}</Text>}
            <ElementPanel runId={runId} scene={scene.data} selected={selected} />
          </Area>
        </Group>
      </Stack>
    </Modal>
  );
}
