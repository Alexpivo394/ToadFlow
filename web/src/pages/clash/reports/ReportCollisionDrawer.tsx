import { Link as RouterLink } from 'react-router';
import { Anchor, Box, Drawer, Stack } from '@mantine/core';
import { useApi } from '../../../api/queries';
import type { CollisionCard as Card } from '../../../api/types';
import { CollisionImage } from '../../../components/CollisionImage';
import { CollisionCard } from '../approved/CollisionCard';

/**
 * Боковая панель коллизии отчёта: картинка 640 × 400, карточка (статус, «кто правит», лента — права как в проверке) и ссылка на
 * 3D в проверке.
 */
export function ReportCollisionDrawer({ partId, collisionId, canEdit, canMerge, onClose }: {
  partId: string;
  collisionId: string | null;
  canEdit: boolean;
  canMerge: boolean;
  onClose: () => void;
}) {
  const card = useApi<Card>(['clash-collisions', 'card', collisionId], collisionId ? `/api/clash/collisions/${collisionId}` : null);
  const c = card.data?.collision;

  return (
    <Drawer opened={collisionId !== null} onClose={onClose} position="right" size={700}
      title={c ? `Коллизия № ${c.number}` : 'Коллизия'}>
      {collisionId && (
        <Stack gap="sm">
          <Box>
            {c && <CollisionImage key={collisionId} collisionId={collisionId} imageVersion={c.imageVersion} width={640} />}
          </Box>
          <Anchor component={RouterLink} to={`/clash/checks/${partId}?collision=${collisionId}`} size="sm">Открыть в 3D</Anchor>
          <Box h={420}>
            <CollisionCard key={collisionId} collisionId={collisionId} canEdit={canEdit} canMerge={canMerge} image={false} />
          </Box>
        </Stack>
      )}
    </Drawer>
  );
}
