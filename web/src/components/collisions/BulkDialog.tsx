import { Alert, Button, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { useState } from 'react';
import type { BulkAction, CollisionAssignee } from '../../api/types';

/** Действие над выделенными коллизиями: что шлётся на сервер и как оно называется в диалоге. */
export type BulkChoice = { action: BulkAction; assignee?: CollisionAssignee; label: string };

/** «коллизию», «коллизии», «коллизий» — после числа в винительном падеже. */
export function collisionsWord(count: number): string {
  const mod10 = count % 10;
  const mod100 = count % 100;
  if (mod10 === 1 && mod100 !== 11) return 'коллизию';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return 'коллизии';
  return 'коллизий';
}

const needsNote = (action: BulkAction) => action === 'Fake' || action === 'Note';

/**
 * Подтверждение массового действия: «Изменить N коллизий: «действие». Продолжить?». У «Фиктивных» и «Примечания» поле
 * примечания обязательно. `conflict` — ответ 409 «все по фильтру»: текст сервера и «Повторить» с новым числом.
 */
export function BulkDialog({ choice, count, busy, conflict, onConfirm, onCancel }: {
  choice: BulkChoice | null;
  count: number;
  busy: boolean;
  conflict: { error: string; count: number } | null;
  onConfirm: (note: string | undefined, expectedCount?: number) => void;
  onCancel: () => void;
}) {
  const [note, setNote] = useState('');
  const opened = choice !== null;
  const noteRequired = choice !== null && needsNote(choice.action);
  const ready = !noteRequired || note.trim().length > 0;
  const close = () => {
    setNote('');
    onCancel();
  };

  return (
    <Modal opened={opened} onClose={close} title="Массовое действие" centered>
      {choice && (
        <Stack gap="sm">
          <Text size="sm">Изменить {count.toLocaleString('ru-RU')} {collisionsWord(count)}: «{choice.label}». Продолжить?</Text>
          {noteRequired && (
            <Textarea label="Примечание" aria-label="Примечание" autosize minRows={2} required value={note}
              description={choice.action === 'Fake' ? 'Одно примечание на все коллизии: почему они фиктивные' : undefined}
              onChange={(e) => setNote(e.currentTarget.value)} />
          )}
          {conflict && <Alert color="orange">{conflict.error}</Alert>}
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={close}>Отмена</Button>
            {conflict ? (
              <Button loading={busy} disabled={!ready} onClick={() => onConfirm(noteRequired ? note.trim() : undefined, conflict.count)}>
                Повторить ({conflict.count.toLocaleString('ru-RU')})
              </Button>
            ) : (
              <Button loading={busy} disabled={!ready} onClick={() => onConfirm(noteRequired ? note.trim() : undefined)}>Продолжить</Button>
            )}
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
