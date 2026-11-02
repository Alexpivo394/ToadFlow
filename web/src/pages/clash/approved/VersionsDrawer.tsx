import { useState } from 'react';
import { Badge, Button, Drawer, Group, Stack, Text } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { ApprovedVersionView, ConfigChange, ConfigChangeKind } from '../../../api/types';
import { formatDateTime } from '../../../lib/format';

/** Значок вида изменения: добавлено, убрано или изменено. */
const mark = (kind: ConfigChangeKind) =>
  kind.endsWith('Added') || kind === 'CellEnabled' ? '+' : kind.endsWith('Removed') || kind === 'CellDisabled' ? '−' : '~';

/** Отличия версии от предыдущей — загружаются, когда список раскрыт. */
function VersionChanges({ partId, version }: { partId: string; version: number }) {
  const changes = useApi<ConfigChange[]>(['clash-parts', partId, 'versions', version, 'changes'], `/api/clash/parts/${partId}/versions/${version}/changes`);
  if (!changes.data) return <Text size="xs" c="dimmed">…</Text>;
  if (changes.data.length === 0) return <Text size="xs" c="dimmed">{version === 1 ? 'Первая версия' : 'Настройка не менялась'}</Text>;
  return (
    <Stack gap={0}>
      {changes.data.map((c, i) => <Text key={i} size="xs">{mark(c.kind)} {c.text}</Text>)}
    </Stack>
  );
}

/** Утверждённые версии части, новые сверху: кто, когда, с каким комментарием и что изменилось относительно предыдущей. */
export function VersionsDrawer({ partId, versions, onClose }: { partId: string; versions: ApprovedVersionView[]; onClose: () => void }) {
  const [open, setOpen] = useState<Set<number>>(new Set());
  const toggle = (version: number) => setOpen((s) => {
    const next = new Set(s);
    if (next.has(version)) next.delete(version); else next.add(version);
    return next;
  });

  return (
    <Drawer opened onClose={onClose} position="right" title="Утверждённые версии">
      <Stack gap="md">
        {versions.map((v, i) => (
          <Stack key={v.version} gap={2}>
            <Group gap="xs">
              <Badge variant={i === 0 ? 'filled' : 'light'}>v{v.version}</Badge>
              <Text size="sm">{formatDateTime(v.approvedAt)} · {v.userName}</Text>
              {i === 0 && <Text size="xs" c="dimmed">действует</Text>}
            </Group>
            {v.comment && <Text size="sm">{v.comment}</Text>}
            <Button size="compact-xs" variant="subtle" w="fit-content" onClick={() => toggle(v.version)}>Что изменилось</Button>
            {open.has(v.version) && <VersionChanges partId={partId} version={v.version} />}
          </Stack>
        ))}
      </Stack>
    </Drawer>
  );
}
