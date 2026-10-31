import { Center, Image, Loader, Text } from '@mantine/core';
import { useImage } from '../api/useImage';

/**
 * Картинка коллизии (640 × 400): сервер рисует её по запросу. Пока грузится — индикатор; если не получилась — причина
 * из ответа сервера.
 */
export function CollisionImage({ collisionId, imageVersion, width = 320 }: {
  collisionId: string;
  imageVersion: string;
  width?: number;
}) {
  const image = useImage(`/api/clash/collisions/${collisionId}/image?v=${imageVersion}`);
  const height = Math.round((width * 400) / 640);
  if (image.url) return <Image src={image.url} w={width} h={height} radius="sm" alt="Картинка коллизии" />;

  return (
    <Center w={width} h={height} bg="var(--mantine-color-gray-1)" style={{ borderRadius: 4, flexShrink: 0 }}>
      {image.isLoading ? <Loader size="xs" /> : <Text size="xs" c="dimmed" ta="center" px="xs">{image.error}</Text>}
    </Center>
  );
}
