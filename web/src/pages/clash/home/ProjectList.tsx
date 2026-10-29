import { useState } from 'react';
import { Badge, NavLink, ScrollArea, Stack, TextInput } from '@mantine/core';
import type { ClashProjectView } from '../../../api/types';

const storageKey = 'toadflow.clash.project';

/** Выбранный проект — ключ: объект или '' для «Без проекта». */
export type ProjectKey = string;

export const projectKey = (project: ClashProjectView): ProjectKey => project.object ?? '';

/** Последний открытый проект живёт в браузере: страница открывается там, где её оставили. */
export function storedProject(): ProjectKey | null {
  try {
    return localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

export function storeProject(key: ProjectKey) {
  try {
    localStorage.setItem(storageKey, key);
  } catch {
    // хранилище недоступно — выбор проживёт до перезагрузки
  }
}

/** Список проектов слева: поиск и число частей или моделей. */
export function ProjectList({ projects, selected, onSelect, count }: {
  projects: ClashProjectView[];
  selected: ProjectKey | null;
  onSelect: (key: ProjectKey) => void;
  count: (project: ClashProjectView) => number;
}) {
  const [search, setSearch] = useState('');
  const wanted = search.trim().toLowerCase();
  const shown = projects.filter((p) => !wanted || p.name.toLowerCase().includes(wanted));
  return (
    <Stack gap={6} w={240} style={{ flexShrink: 0 }}>
      <TextInput size="xs" placeholder="Поиск проекта" value={search} onChange={(e) => setSearch(e.currentTarget.value)} />
      <ScrollArea.Autosize mah="calc(100vh - 220px)">
        {shown.map((p) => (
          <NavLink key={projectKey(p)} label={p.name} active={selected === projectKey(p)} c={p.object === null ? 'dimmed' : undefined}
            rightSection={<Badge size="sm" variant="light" color="gray">{count(p)}</Badge>} onClick={() => onSelect(projectKey(p))} />
        ))}
      </ScrollArea.Autosize>
    </Stack>
  );
}
