import { lazy, Suspense, useEffect, useState } from 'react';
import { Badge, Box as Area, Button, Center, Group, List, Loader, Paper, ScrollArea, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch, apiFetchBinary } from '../../../api/client';
import type { ViewerElementView, ViewerSceneView } from '../../../api/types';
import { categoryLabel } from '../../../lib/categories';
import type { ClashConfig } from '../../../lib/clashConfig';
import { parseMeshes, roleColor } from '../../../lib/viewer';

const ClashViewer = lazy(() => import('../viewer/ClashViewer'));

const n = (value: number) => value.toLocaleString('ru-RU');

/** Образец набора: модель (ключ модели в выгрузке) и UniqueId. */
export interface SetFocus {
  model: string;
  uniqueId: string;
}

/** «Почему в наборе»: какие группы условий набора отобрали элемент — по несохранённому черновику. */
function Why({ partId, body, element }: { partId: string; body: { configuration: ClashConfig; setId: string }; element: ViewerElementView }) {
  const why = useQuery({
    queryKey: ['clash-set-why', partId, body, element.modelId, element.uniqueId],
    queryFn: () => apiFetch<string[]>(`/api/clash/parts/${partId}/draft/sets/why`, {
      method: 'POST', json: { ...body, model: element.modelId, uniqueId: element.uniqueId },
    }),
  });
  return (
    <Stack gap={4}>
      <Text size="sm" fw={600}>Почему в наборе</Text>
      {why.error && <Text size="xs" c="red">{why.error.message}</Text>}
      {why.data && (why.data.length > 0
        ? <List size="xs">{why.data.map((line, i) => <List.Item key={i}>{line}</List.Item>)}</List>
        : <Text size="xs" c="dimmed">Ни одна группа условий не отбирает этот элемент</Text>)}
      {!why.data && !why.error && <Loader size="xs" />}
    </Stack>
  );
}

function Brief({ element }: { element: ViewerElementView }) {
  return (
    <Stack gap={2}>
      <Group gap="xs">
        <Badge color={roleColor(element.role).color} variant="filled" size="sm">{element.role === 'set' ? 'Элемент набора' : 'Окружение'}</Badge>
        <Text size="xs" c="dimmed">{element.discipline}</Text>
      </Group>
      <Text size="sm">{categoryLabel(element.category)} · {element.family ?? '—'} · {element.type ?? '—'}</Text>
      <Text size="xs" c="dimmed">{element.modelName} · ID {element.elementId} · {element.level ?? 'без уровня'}</Text>
    </Stack>
  );
}

/**
 * Набор в 3D: элементы набора из последних выгрузок (цветом набора), вокруг образца — окружение серым. Тело запроса —
 * несохранённый черновик на момент открытия; «Обновить» берёт черновик заново. Щелчок по элементу — кратко о нём и
 * «Почему в наборе».
 */
export function SetScene({ partId, config, setId, focus }: { partId: string; config: ClashConfig; setId: string; focus: SetFocus | null }) {
  const [body, setBody] = useState(() => ({ configuration: config, setId, ...(focus ? { focus } : {}) }));
  const [selected, setSelected] = useState<number | null>(null);
  const scene = useQuery({
    queryKey: ['clash-set-scene', partId, body],
    queryFn: () => apiFetch<ViewerSceneView>(`/api/clash/parts/${partId}/draft/sets/scene`, { method: 'POST', json: body }),
  });
  const meshes = useQuery({
    queryKey: ['clash-set-meshes', partId, body],
    queryFn: async () => parseMeshes(await apiFetchBinary(`/api/clash/parts/${partId}/draft/sets/meshes`, body)),
    enabled: !!scene.data,
  });

  // Образец выбран сразу: его свойства и «почему в наборе» видны без щелчка.
  const focusIndex = focus ? scene.data?.elements.find((e) => e.modelId === focus.model && e.uniqueId === focus.uniqueId)?.index ?? null : null;
  useEffect(() => setSelected(focusIndex), [focusIndex, scene.data]);

  // Черновик не менялся — ключ запроса тот же, и новое тело ничего не перезапросит: тогда перезапрос явно (выгрузки могли обновиться).
  const refresh = () => {
    const next = { ...body, configuration: config };
    if (JSON.stringify(next) !== JSON.stringify(body)) setBody(next);
    else void scene.refetch().then(() => meshes.refetch());
  };

  const members = scene.data?.elements.filter((e) => e.role === 'set').length ?? 0;
  const element = selected != null ? scene.data?.elements[selected] : undefined;

  return (
    <Stack gap={6} style={{ flex: 1, minHeight: 0 }}>
      <Group gap="sm">
        {scene.data && (scene.data.truncated
          ? <Text size="sm">Показано {n(members)} из {n(scene.data.total ?? members)}</Text>
          : <Text size="sm">Элементов набора: {n(members)}</Text>)}
        <Button size="compact-xs" variant="subtle" ml="auto" onClick={refresh}>Обновить</Button>
      </Group>
      <Area style={{ flex: 1, minHeight: 400, position: 'relative', border: '1px solid var(--mantine-color-default-border)', borderRadius: 8, overflow: 'hidden' }}>
        {scene.error ? (
          <Center h="100%"><Text c="red">{scene.error.message}</Text></Center>
        ) : scene.data ? (
          // Холст — во всю область: у области только minHeight, процентная высота от неё не считается.
          <div style={{ position: 'absolute', inset: 0 }}>
            <Suspense fallback={<Center h="100%"><Loader /></Center>}>
              <ClashViewer scene={scene.data} meshes={meshes.data} isolation="all" sectionBox={null} selected={selected} onPick={setSelected}
                frame={focusIndex} />
            </Suspense>
          </div>
        ) : (
          <Center h="100%"><Loader /></Center>
        )}
        {element && (
          <Paper withBorder shadow="sm" p="xs" radius="md" w={300} style={{ position: 'absolute', top: 8, right: 8, maxHeight: 'calc(100% - 16px)', display: 'flex' }}>
            <ScrollArea.Autosize mah="100%" style={{ flex: 1 }}>
              <Stack gap="sm">
                <Brief element={element} />
                {element.role === 'set' && <Why partId={partId} body={{ configuration: body.configuration, setId }} element={element} />}
              </Stack>
            </ScrollArea.Autosize>
          </Paper>
        )}
      </Area>
    </Stack>
  );
}
