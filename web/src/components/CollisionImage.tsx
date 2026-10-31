import { Center, Image, Loader, Text } from '@mantine/core';
import { useImage } from '../api/useImage';

/**
 * Картинка коллизии (640 × 400, сервер рисует её после прогона). Без версии картинки — заглушка: «картинка ещё рисуется», если
 * коллизия найдена последним прогоном (работник догонит), иначе «нет картинки».
 */
export function CollisionImage({ collisionId, imageVersion, foundNow, width = 320 }: {
  collisionId: string;
  imageVersion: string | null | undefined;
  foundNow: boolean;
  width?: number;
}) {
  const image = useImage(imageVersion ? `/api/clash/collisions/${collisionId}/image?v=${imageVersion}` : null);
  const height = Math.round((width * 400) / 640);
  if (image.url) return <Image src={image.url} w={width} h={height} radius="sm" alt="Картинка коллизии" />;

  return (
    <Center w={width} h={height} bg="var(--mantine-color-gray-1)" style={{ borderRadius: 4, flexShrink: 0 }}>
      {image.isLoading ? <Loader size="xs" /> : (
        <Text size="xs" c="dimmed" ta="center">{!imageVersion && foundNow ? 'картинка ещё рисуется' : 'нет картинки'}</Text>
      )}
    </Center>
  );
}
