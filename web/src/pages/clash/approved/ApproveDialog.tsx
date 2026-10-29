import { useState } from 'react';
import { Alert, Button, Checkbox, Group, List, Modal, Stack, Text, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, apiFetch } from '../../../api/client';
import type { ApprovedVersionView } from '../../../api/types';

/**
 * «Утвердить версию»: черновик становится новой утверждённой версией, по ней работают расписания. Несохранённые правки
 * сначала сохраняются. По умолчанию утверждённая версия сразу прогоняется на последних выгрузках.
 */
export function ApproveDialog({ partId, nextVersion, dirty, onSave, onClose }: {
  partId: string;
  nextVersion: number;
  dirty: boolean;
  onSave?: () => Promise<void>;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [comment, setComment] = useState('');
  const [run, setRun] = useState(true);
  const [busy, setBusy] = useState(false);
  const [problems, setProblems] = useState<string[]>([]);

  async function approve() {
    setBusy(true);
    setProblems([]);
    try {
      if (dirty && onSave) await onSave();
      const result = await apiFetch<ApprovedVersionView>(`/api/clash/parts/${partId}/approve`, { method: 'POST', json: { comment, run } });
      notifications.show({ color: 'green', message: `Утверждена версия ${result.version}${result.runId ? ', прогон поставлен в очередь' : ''}` });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
      await queryClient.invalidateQueries({ queryKey: ['clash-collisions'] });
      onClose();
    } catch (error) {
      const body = error instanceof ApiError ? (error.body as { errors?: string[] } | null) : null;
      setProblems(body?.errors?.length ? body.errors : [(error as Error).message]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal opened onClose={onClose} title={`Утвердить версию ${nextVersion}`}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          Черновик станет утверждённой версией {nextVersion}. Расписания и коллизии в базе работают по утверждённой версии; черновик можно править дальше.
        </Text>
        {dirty && <Text size="sm" c="orange">Несохранённые правки будут сохранены перед утверждением.</Text>}
        <Textarea label="Комментарий" placeholder="Что изменилось" autosize minRows={2} value={comment} onChange={(e) => setComment(e.currentTarget.value)} />
        <Checkbox label="Сразу прогнать утверждённую версию" checked={run} onChange={(e) => setRun(e.currentTarget.checked)} />
        {problems.length > 0 && (
          <Alert color="red" title="Утвердить нельзя">
            <List size="sm">{problems.map((p) => <List.Item key={p}>{p}</List.Item>)}</List>
          </Alert>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Отмена</Button>
          <Button loading={busy} onClick={approve}>Утвердить</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
