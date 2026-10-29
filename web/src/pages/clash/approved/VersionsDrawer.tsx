import { Badge, Drawer, Group, Stack, Text } from '@mantine/core';
import type { ApprovedVersionView } from '../../../api/types';
import { formatDateTime } from '../../../lib/format';

/** Утверждённые версии части, новые сверху: кто, когда и с каким комментарием. */
export function VersionsDrawer({ versions, onClose }: { versions: ApprovedVersionView[]; onClose: () => void }) {
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
          </Stack>
        ))}
      </Stack>
    </Drawer>
  );
}
