import { Link as RouterLink } from 'react-router';
import { ActionIcon, Anchor, Box, Drawer, Group, Text } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { CollisionCard as Card } from '../../../api/types';
import { CollisionImage } from '../../../components/CollisionImage';
import { CollisionCard } from '../approved/CollisionCard';

/** Панель на всю высоту окна: картинка и ссылка сверху, карточка тянется до низа, лента прокручивается внутри неё. */
const fullHeight = {
  content: { display: 'flex', flexDirection: 'column' as const },
  body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' as const, gap: 'var(--mantine-spacing-sm)' },
};

/**
 * Боковая панель коллизии отчёта: картинка 640 × 400, ссылка на 3D в проверке и карточка (статус, «кто правит», лента — права
 * как в проверке). Кнопки «‹ ›» — соседние коллизии списка; без обработчика кнопка выключена.
 */
export function ReportCollisionDrawer({ partId, opened, collisionId, canEdit, canMerge, onClose, onPrev, onNext }: {
  partId: string;
  opened: boolean;
  collisionId: string | null;
  canEdit: boolean;
  canMerge: boolean;
  onClose: () => void;
  onPrev?: () => void;
  onNext?: () => void;
}) {
  const card = useApi<Card>(['clash-collisions', 'card', collisionId], collisionId ? `/api/clash/collisions/${collisionId}` : null);
  const c = card.data?.collision;

  return (
    <Drawer opened={opened} onClose={onClose} position="right" size={700} styles={fullHeight}
      title={(
        <Group gap="xs" wrap="nowrap">
          <ActionIcon variant="default" size="sm" aria-label="Предыдущая коллизия" disabled={!onPrev} onClick={onPrev}>‹</ActionIcon>
          <ActionIcon variant="default" size="sm" aria-label="Следующая коллизия" disabled={!onNext} onClick={onNext}>›</ActionIcon>
          <Text fw={600}>{c ? `Коллизия № ${c.number}` : 'Коллизия'}</Text>
        </Group>
      )}>
      {collisionId && (
        <>
          <Box style={{ flex: 'none' }}>
            {c && <CollisionImage key={collisionId} collisionId={collisionId} imageVersion={c.imageVersion} width={640} />}
          </Box>
          <Anchor component={RouterLink} to={`/clash/checks/${partId}?collision=${collisionId}`} size="sm" style={{ flex: 'none' }}>
            Открыть в 3D
          </Anchor>
          <Box data-testid="drawer-card" style={{ flex: 1, minHeight: 0 }}>
            <CollisionCard key={collisionId} collisionId={collisionId} canEdit={canEdit} canMerge={canMerge} image={false} />
          </Box>
        </>
      )}
    </Drawer>
  );
}
