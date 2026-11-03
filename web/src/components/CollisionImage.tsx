import { useEffect, useRef, useState } from 'react';
import { Center, Image, Loader, Text } from '@mantine/core';
import { useImage } from '../api/useImage';

/**
 * Видна ли область (с запасом 200 px): картинки длинных списков грузятся, когда до них долистали. Без IntersectionObserver
 * (старый браузер) — видна сразу.
 */
function useVisible<T extends Element>() {
  const ref = useRef<T | null>(null);
  const [visible, setVisible] = useState(() => typeof IntersectionObserver === 'undefined');

  useEffect(() => {
    if (visible || !ref.current || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        setVisible(true);
        observer.disconnect();
      }
    }, { rootMargin: '200px' });
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, [visible]);

  return { ref, visible };
}

/**
 * Картинка коллизии (640 × 400): сервер рисует её по запросу. Грузится, когда видна; пока грузится — индикатор; если не
 * получилась — причина из ответа сервера.
 */
export function CollisionImage({ collisionId, imageVersion, width = 320 }: {
  collisionId: string;
  imageVersion: string;
  width?: number;
}) {
  const { ref, visible } = useVisible<HTMLDivElement>();
  const image = useImage(`/api/clash/collisions/${collisionId}/image?v=${imageVersion}`, { enabled: visible });
  const height = Math.round((width * 400) / 640);

  return (
    <div ref={ref} style={{ width, height, flexShrink: 0 }}>
      {image.url ? (
        <Image src={image.url} w={width} h={height} radius="sm" alt="Картинка коллизии" />
      ) : (
        <Center w={width} h={height} bg="var(--mantine-color-gray-1)" style={{ borderRadius: 4 }}>
          {image.isLoading ? <Loader size="xs" /> : <Text size="xs" c="dimmed" ta="center" px="xs">{image.error}</Text>}
        </Center>
      )}
    </div>
  );
}
