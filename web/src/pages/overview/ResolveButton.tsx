import { useState } from 'react';
import { Button, Group, Modal, Stack, Text, Textarea, Tooltip, UnstyledButton } from '@mantine/core';
import { IconCheck } from '@tabler/icons-react';
import { useApiMutation } from '../../api/queries';
import { t } from '../../i18n/ru';

const MaxNote = 500;

/**
 * «Решено» у ошибок модуля — общее для «Требует внимания» и «Для вас»: ошибки до этого момента пропадают у всех. Без примечания
 * не отметить — кто и почему, видно в ленте «Что происходит». <c>chip</c> — кнопка вплотную к плашке «Требует внимания».
 */
export function ResolveButton({ resolveKey, title, variant, level }: { resolveKey: string; title: string; variant: 'chip' | 'row'; level?: string }) {
  const [opened, setOpened] = useState(false);
  const [note, setNote] = useState('');
  const close = () => {
    setOpened(false);
    setNote('');
  };
  const resolve = useApiMutation<string>('POST', () => `/api/overview/attention/${encodeURIComponent(resolveKey)}/resolve`, {
    invalidate: ['overview', 'overview-feed', 'me-today'],
    success: t.attention.resolved,
    json: (text) => ({ note: text }),
  });
  const trimmed = note.trim();

  return (
    <>
      <Tooltip label={t.attention.resolveHint} withArrow multiline w={260}>
        {variant === 'chip' ? (
          <UnstyledButton className="ov-chip-resolve" data-level={level} onClick={() => setOpened(true)}>
            <IconCheck size={16} />
            {t.attention.resolve}
          </UnstyledButton>
        ) : (
          <Button size="compact-sm" variant="default" leftSection={<IconCheck size={14} />} onClick={() => setOpened(true)}>
            {t.attention.resolve}
          </Button>
        )}
      </Tooltip>
      <Modal opened={opened} onClose={close} title={title} centered>
        <Stack gap="sm">
          <Text size="sm" c="dimmed">{t.attention.resolveHint}</Text>
          <Textarea label={t.attention.note} placeholder={t.attention.notePlaceholder} autosize minRows={2} maxLength={MaxNote} data-autofocus
            value={note} onChange={(e) => setNote(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={close}>{t.attention.cancel}</Button>
            <Button disabled={trimmed.length === 0} loading={resolve.isPending}
              onClick={() => resolve.mutate(trimmed, { onSuccess: close })}>
              {t.attention.submit}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
