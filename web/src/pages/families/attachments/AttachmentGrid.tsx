import { ActionIcon, Box, Card, Center, Image, Loader, SimpleGrid, Stack, Text, Tooltip } from '@mantine/core';
import { IconFile, IconFileTypePdf, IconX } from '@tabler/icons-react';
import { useImage } from '../../../api/useImage';
import { t } from '../../../i18n/ru';
import { fileSize, type FileKind } from './kinds';

/** Плитка вложения: местная загрузка (<c>previewUrl</c>) или файл сервера (<c>path</c> — адрес для миниатюры). */
export interface GridItem {
  key: string;
  name: string;
  size: number;
  kind: FileKind;
  previewUrl?: string | null;
  path?: string | null;
  error?: string | null;
  busy?: boolean;
  id?: string | null;
}

function Thumb({ item }: { item: GridItem }) {
  const image = useImage(item.kind === 'image' && !item.previewUrl && item.path ? item.path : null);
  const url = item.previewUrl ?? image.url;
  if (item.kind === 'image' && url) return <Image src={url} h={96} fit="cover" radius="sm" alt={item.name} />;
  return (
    <Center h={96} bg="var(--mantine-color-default-hover)" style={{ borderRadius: 'var(--mantine-radius-sm)' }}>
      {item.kind === 'pdf' ? <IconFileTypePdf size={36} stroke={1.3} /> : <IconFile size={36} stroke={1.3} />}
    </Center>
  );
}

/** Сетка миниатюр: щелчок — просмотр, крестик — убрать (если можно). */
export function AttachmentGrid({ items, onOpen, onRemove, cols = { base: 2, xs: 3, sm: 4 } }: {
  items: GridItem[];
  onOpen?: (index: number) => void;
  onRemove?: (key: string) => void;
  cols?: Record<string, number> | number;
}) {
  if (items.length === 0) return null;
  return (
    <SimpleGrid cols={cols} spacing="xs">
      {items.map((item, index) => {
        const busy = item.busy ?? (item.id === null && !item.error);
        return (
          <Card key={item.key} withBorder padding={4} radius="md" style={{ cursor: onOpen ? 'pointer' : undefined, position: 'relative' }}
            onClick={() => onOpen?.(index)}>
            <Box pos="relative">
              <Thumb item={item} />
              {busy && (
                <Center pos="absolute" inset={0} bg="rgba(0,0,0,0.25)" style={{ borderRadius: 'var(--mantine-radius-sm)' }}>
                  <Loader size="sm" color="white" />
                </Center>
              )}
            </Box>
            <Stack gap={0} px={4} pt={4}>
              <Text size="xs" fw={500} truncate title={item.name}>{item.name}</Text>
              <Text size="xs" c={item.error ? 'red' : 'dimmed'} truncate>{item.error ?? fileSize(item.size)}</Text>
            </Stack>
            {onRemove && (
              <Tooltip label={t.attach.remove}>
                <ActionIcon size="sm" variant="filled" color="dark" radius="xl" pos="absolute" top={8} right={8} aria-label={`${t.attach.remove} ${item.name}`}
                  onClick={(e) => { e.stopPropagation(); onRemove(item.key); }}>
                  <IconX size={12} />
                </ActionIcon>
              </Tooltip>
            )}
          </Card>
        );
      })}
    </SimpleGrid>
  );
}
