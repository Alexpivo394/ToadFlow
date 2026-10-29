import { lazy, Suspense, useEffect, useState } from 'react';
import { Badge, Box as Area, Button, Center, Checkbox, Group, Loader, Paper, RangeSlider, ScrollArea, SegmentedControl, Select, Stack, Text } from '@mantine/core';
import { useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiFetch, apiFetchBinary } from '../../../api/client';
import type { ViewerSceneView } from '../../../api/types';
import { type Box, defaultSectionBox, type Isolation, parseMeshes } from '../../../lib/viewer';
import { ElementPanel } from './ElementPanel';

// three.js тяжёлый: грузится отдельным чанком, только когда открыт просмотрщик.
const ClashViewer = lazy(() => import('./ClashViewer'));

const Radii = ['3000', '5000', '10000', '20000'];
const axes = ['X', 'Y', 'Z'] as const;

const base = (runId: string, number: number) => `/api/clash/runs/${runId}/hits/${number}`;

export const sceneQuery = (runId: string, number: number, radius: string) => ({
  queryKey: ['clash-viewer', runId, number, radius, 'scene'],
  queryFn: () => apiFetch<ViewerSceneView>(`${base(runId, number)}/scene?radius=${radius}`),
});

export const meshesQuery = (runId: string, number: number, radius: string) => ({
  queryKey: ['clash-viewer', runId, number, radius, 'meshes'],
  queryFn: async () => parseMeshes(await apiFetchBinary(`${base(runId, number)}/meshes?radius=${radius}`)),
});

/** Заранее грузит сцену и сетки коллизии — листание стрелками не ждёт сервер. */
export function prefetchHit(client: QueryClient, runId: string, number: number, radius = '5000') {
  void client.prefetchQuery(sceneQuery(runId, number, radius));
  void client.prefetchQuery(meshesQuery(runId, number, radius));
}

function plural(n: number, one: string, few: string, many: string) {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}

/**
 * Сцена коллизии: 3D (A и B, окружение в радиусе), изоляция, секущий куб и свойства элемента плашкой поверх сцены.
 * Встраивается в черновик (и позже в отчёт); <c>focus</c> — какую сторону показать в свойствах (Enter в списке).
 */
export function HitScene({ runId, number, title, focus, onRefresh }: {
  runId: string;
  number: number;
  title?: string;
  focus?: 'A' | 'B' | null;
  onRefresh?: () => void;
}) {
  const queryClient = useQueryClient();
  const [radius, setRadius] = useState('5000');
  const [isolation, setIsolation] = useState<Isolation>('all');
  const [sectionOn, setSectionOn] = useState(false);
  const [section, setSection] = useState<Box | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [showProps, setShowProps] = useState(true);

  const scene = useQuery(sceneQuery(runId, number, radius));
  const meshes = useQuery({ ...meshesQuery(runId, number, radius), enabled: !!scene.data });

  // Другая коллизия — новый секущий куб вокруг её пары и сброс выбора.
  useEffect(() => {
    setSelected(null);
    setSection(scene.data ? defaultSectionBox(scene.data) : null);
  }, [scene.data]);

  useEffect(() => {
    if (!focus || !scene.data) return;
    const element = scene.data.elements.find((e) => e.role === focus);
    if (element) {
      setSelected(element.index);
      setShowProps(true);
    }
  }, [focus, scene.data]);

  const context = scene.data?.elements.filter((e) => e.role === 'context') ?? [];
  const withMesh = context.filter((e) => e.mesh).length;
  const limit = scene.data?.radiusMm ?? Number(radius);
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
    onRefresh?.();
  };

  return (
    <Stack gap={6} h="100%" style={{ minWidth: 0 }}>
      <Group gap="sm" wrap="wrap">
        {title && <Text size="sm" fw={600}>{title}</Text>}
        <SegmentedControl size="xs" value={isolation} onChange={(v) => setIsolation(v as Isolation)}
          data={[{ value: 'all', label: 'Всё' }, { value: 'pair', label: 'Только A и B' }, { value: 'selected', label: 'Только выбранный' }]} />
        <Select size="xs" w={140} allowDeselect={false} value={radius} onChange={(v) => v && setRadius(v)} aria-label="Радиус окружения"
          data={Radii.map((r) => ({ value: r, label: `окружение ${Number(r) / 1000} м` }))} />
        <Checkbox size="xs" label="Секущий куб" checked={sectionOn} onChange={(e) => setSectionOn(e.currentTarget.checked)} />
        {scene.data && (
          <Text size="xs" c="dimmed">
            Окружение: {context.length} {plural(context.length, 'элемент', 'элемента', 'элементов')}, {withMesh} с сеткой
          </Text>
        )}
        {scene.data?.truncated && <Badge color="orange" variant="light">окружение урезано</Badge>}
        <Button size="compact-xs" variant="subtle" ml="auto" onClick={() => setShowProps(!showProps)}>{showProps ? 'Скрыть свойства' : 'Свойства'}</Button>
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

      <Area style={{ flex: 1, minHeight: 0, position: 'relative', border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
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
              selected={selected} onPick={setSelected} onSectionChange={setSection} />
          </Suspense>
        ) : (
          <Center h="100%"><Loader /></Center>
        )}
        {showProps && scene.data && (
          <Paper withBorder shadow="sm" p="xs" radius="md" w={320} style={{ position: 'absolute', top: 8, right: 8, maxHeight: 'calc(100% - 16px)', display: 'flex' }}>
            <ScrollArea.Autosize mah="100%" style={{ flex: 1 }}>
              {meshes.error && <Text size="xs" c="red">{meshes.error.message}</Text>}
              <ElementPanel runId={runId} scene={scene.data} selected={selected} />
            </ScrollArea.Autosize>
          </Paper>
        )}
      </Area>
    </Stack>
  );
}
