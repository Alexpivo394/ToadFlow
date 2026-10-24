import { useState } from 'react';
import { Alert, Box, Checkbox, Group, Loader, Text, UnstyledButton } from '@mantine/core';
import { useQueries } from '@tanstack/react-query';
import { IconChevronDown, IconChevronRight, IconFile, IconFolder } from '@tabler/icons-react';
import { apiFetch, ApiError } from '../../api/client';
import type { SourceEntry } from '../../api/types';
import { isChecked, setFormats, toggleFolder, toggleModel, type SelectionState } from './selection';

interface Props {
  sourceId: string;
  rootPath: string;
  selection: SelectionState;
  includeNew: boolean;
  readOnly?: boolean;
  /** Галки RVT и NWC у отмеченных — только у выгрузки; другим задачам формат не нужен. */
  formats?: boolean;
  onChange: (selection: SelectionState) => void;
}

/**
 * Дерево источника с галками. Папки загружаются по раскрытию — через агента, потому что сервер сетевые папки не видит.
 * Нет агентов — показывается сообщение, а уже отмеченное остаётся и сохраняется.
 */
export function SourceTree({ sourceId, rootPath, selection, includeNew, readOnly, formats = true, onChange }: Props) {
  const [expanded, setExpanded] = useState<string[]>([rootPath]);
  const results = useQueries({
    queries: expanded.map((path) => ({
      queryKey: ['source-tree', sourceId, path],
      queryFn: () => apiFetch<SourceEntry[]>(`/api/sources/${sourceId}/tree?path=${encodeURIComponent(path)}`),
      staleTime: 60_000,
      retry: false,
    })),
  });
  const children = new Map(expanded.map((path, i) => [path, results[i]]));
  const loaded = results.flatMap((r) => r.data ?? []);
  const rootError = children.get(rootPath)?.error;

  if (rootError) {
    return (
      <Alert color="yellow" title="Дерево недоступно">
        {rootError instanceof ApiError && rootError.status === 503
          ? 'Нет агентов в сети, которые видят этот источник. Отмеченное ранее сохранится.'
          : rootError.message}
      </Alert>
    );
  }

  function toggleExpand(path: string) {
    setExpanded((current) => (current.includes(path) ? current.filter((p) => p !== path) : [...current, path]));
  }

  function renderLevel(path: string, depth: number) {
    const query = children.get(path);
    if (!query || query.isLoading) return <Loader size="xs" ml={depth * 18 + 24} my={4} />;
    return (query.data ?? []).map((entry) => {
      const key = entry.isFolder ? entry.folderPath : entry.modelPath;
      const checked = isChecked(selection, entry);
      const own = entry.isFolder ? selection.folders[entry.folderPath] : selection.models[entry.modelPath];
      const open = entry.isFolder && expanded.includes(entry.folderPath);

      return (
        <Box key={key}>
          <Group gap={6} wrap="nowrap" pl={depth * 18} py={2}>
            {entry.isFolder ? (
              <UnstyledButton onClick={() => toggleExpand(entry.folderPath)} aria-label={open ? 'Свернуть' : 'Развернуть'}>
                {open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
              </UnstyledButton>
            ) : (
              <Box w={14} />
            )}
            <Checkbox
              size="xs"
              checked={checked}
              disabled={readOnly}
              onChange={(e) => {
                const on = e.currentTarget.checked;
                if (entry.isFolder) {
                  if (on && !includeNew && !open) toggleExpand(entry.folderPath);
                  onChange(toggleFolder(selection, entry.folderPath, on, includeNew, loaded));
                } else onChange(toggleModel(selection, entry, on));
              }}
            />
            {entry.isFolder ? <IconFolder size={16} color="var(--mantine-color-yellow-6)" /> : <IconFile size={16} color="var(--mantine-color-blue-5)" />}
            <Text size="sm" truncate style={{ flex: 1 }}>
              {entry.name}
            </Text>
            {own && formats && (
              <Group gap={6} wrap="nowrap">
                <Checkbox size="xs" label="RVT" checked={own.rvt} disabled={readOnly} onChange={(e) => onChange(setFormats(selection, key, { rvt: e.currentTarget.checked, nwc: own.nwc }))} />
                <Checkbox size="xs" label="NWC" checked={own.nwc} disabled={readOnly} onChange={(e) => onChange(setFormats(selection, key, { rvt: own.rvt, nwc: e.currentTarget.checked }))} />
              </Group>
            )}
          </Group>
          {open && renderLevel(entry.folderPath, depth + 1)}
        </Box>
      );
    });
  }

  return <Box>{renderLevel(rootPath, 0)}</Box>;
}
