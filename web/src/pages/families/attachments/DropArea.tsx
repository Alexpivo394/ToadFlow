import { useRef, useState } from 'react';
import { Group, Paper, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconPhotoPlus } from '@tabler/icons-react';
import { t } from '../../../i18n/ru';
import { usePasteFiles } from './useUploads';

/**
 * Зона вложений: перетащить файлы, вставить скриншот Ctrl+V (на всей странице) или выбрать. <c>compact</c> — строкой для композера.
 */
export function DropArea({ onFiles, compact = false, paste = true }: { onFiles: (files: File[]) => void; compact?: boolean; paste?: boolean }) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  usePasteFiles(onFiles, paste);

  const pick = (
    <input ref={input} type="file" multiple hidden data-testid="drop-input"
      onChange={(e) => { onFiles(Array.from(e.currentTarget.files ?? [])); e.currentTarget.value = ''; }} />
  );
  const handlers = {
    onDragOver: (e: React.DragEvent) => { e.preventDefault(); setOver(true); },
    onDragLeave: () => setOver(false),
    onDrop: (e: React.DragEvent) => { e.preventDefault(); setOver(false); onFiles(Array.from(e.dataTransfer.files)); },
  };

  if (compact)
    return (
      <UnstyledButton onClick={() => input.current?.click()} {...handlers} aria-label={t.attach.pick}>
        <Group gap={6} c="dimmed">
          <IconPhotoPlus size={16} />
          <Text size="xs">{t.attach.compact}</Text>
        </Group>
        {pick}
      </UnstyledButton>
    );

  return (
    <Paper withBorder p="lg" radius="md" {...handlers} onClick={() => input.current?.click()}
      style={{ borderStyle: 'dashed', cursor: 'pointer', background: over ? 'var(--mantine-primary-color-light)' : undefined, transition: 'background 120ms' }}>
      <Stack gap={4} align="center">
        <IconPhotoPlus size={32} stroke={1.4} color="var(--mantine-primary-color-filled)" />
        <Text fw={600}>{t.attach.title}</Text>
        <Text size="sm" c="dimmed" ta="center">{t.attach.hint}</Text>
      </Stack>
      {pick}
    </Paper>
  );
}
