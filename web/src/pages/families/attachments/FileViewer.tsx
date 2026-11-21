import { useEffect } from 'react';
import { ActionIcon, Button, Center, Group, Image, Loader, Modal, Stack, Text } from '@mantine/core';
import { IconChevronLeft, IconChevronRight, IconDownload } from '@tabler/icons-react';
import { downloadFile } from '../../../api/client';
import { useImage } from '../../../api/useImage';
import { t } from '../../../i18n/ru';
import type { FileKind } from './kinds';

/** Файл для просмотра: <c>url</c> — уже готовый адрес (местная загрузка), иначе <c>path</c> — адрес API для показа. */
export interface ViewerFile {
  key: string;
  name: string;
  kind: FileKind;
  url?: string | null;
  path?: string | null;
  /** Адрес скачивания; null — скачать нельзя. */
  download?: string | null;
}

function Body({ file }: { file: ViewerFile }) {
  const loaded = useImage(file.url || file.kind === 'other' ? null : (file.path ?? null));
  const url = file.url ?? loaded.url;
  if (file.kind === 'other') return <Center h={240}><Text c="dimmed">{t.attach.noPreview}</Text></Center>;
  if (!url) return <Center h={240}>{loaded.error ? <Text c="dimmed">{loaded.error}</Text> : <Loader />}</Center>;
  if (file.kind === 'pdf') return <iframe src={url} title={file.name} style={{ width: '100%', height: '75vh', border: 0 }} />;
  return <Image src={url} alt={file.name} fit="contain" mah="75vh" />;
}

/** Просмотр вложений: картинки и PDF в окне, стрелки (и ← →) листают, остальное — скачать. */
export function FileViewer({ files, index, onIndex, onClose }: {
  files: ViewerFile[];
  index: number | null;
  onIndex: (index: number) => void;
  onClose: () => void;
}) {
  const file = index === null ? null : files[index];
  useEffect(() => {
    if (index === null) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'ArrowRight' && index < files.length - 1) onIndex(index + 1);
      if (e.key === 'ArrowLeft' && index > 0) onIndex(index - 1);
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [index, files.length, onIndex]);

  return (
    <Modal opened={file != null} onClose={onClose} size="xl" centered title={file?.name}>
      {file && index !== null && (
        <Stack gap="sm">
          <Body key={file.key} file={file} />
          <Group justify="space-between">
            <ActionIcon variant="default" size="lg" disabled={index === 0} onClick={() => onIndex(index - 1)} aria-label={t.attach.previous}>
              <IconChevronLeft size={18} />
            </ActionIcon>
            <Group gap="xs">
              <Text size="sm" c="dimmed">{index + 1} из {files.length}</Text>
              {file.download && (
                <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} onClick={() => void downloadFile(file.download!, file.name)}>
                  {t.attach.download}
                </Button>
              )}
            </Group>
            <ActionIcon variant="default" size="lg" disabled={index === files.length - 1} onClick={() => onIndex(index + 1)} aria-label={t.attach.next}>
              <IconChevronRight size={18} />
            </ActionIcon>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
