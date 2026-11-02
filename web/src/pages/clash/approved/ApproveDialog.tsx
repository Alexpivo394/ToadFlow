import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { Alert, Anchor, Button, Checkbox, Divider, Group, List, Modal, Stack, Text, Textarea } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, apiFetch } from '../../../api/client';
import { useApi } from '../../../api/queries';
import type { ApprovedVersionView, PartScheduleView } from '../../../api/types';
import { describeRecurrence } from '../../schedules/recurrenceText';
import { ApprovePreviewBlock } from './ApprovePreviewBlock';

/**
 * «Регулярный прогон»: флажки расписаний выгрузки для коллизий, отмеченные — включены. Части без расписания блок
 * подсвечивает; без единого такого расписания вместо флажков — ссылка на создание.
 */
function ScheduleChoice({ schedules, value, onChange }: { schedules: PartScheduleView[]; value: string[]; onChange: (ids: string[]) => void }) {
  const none = !schedules.some((s) => s.selected);
  return (
    <Stack gap={6} p={none ? 'xs' : 0} bg={none ? 'var(--mantine-color-yellow-light)' : undefined} style={{ borderRadius: 6 }}>
      <Text fw={700} size="sm">Регулярный прогон</Text>
      {none && <Text size="sm">У части нет расписания — после утверждения её никто не прогонит</Text>}
      {schedules.length === 0 ? (
        <Anchor component={RouterLink} to="/schedules/new" size="sm">Создать расписание</Anchor>
      ) : (
        schedules.map((s) => (
          <Checkbox key={s.id} size="sm" checked={value.includes(s.id)}
            label={`${s.name} — ${describeRecurrence(s)}${s.isEnabled ? '' : ' (выключено)'}`}
            onChange={(e) => {
              const checked = e.currentTarget.checked;
              onChange(checked ? [...value, s.id] : value.filter((id) => id !== s.id));
            }} />
        ))
      )}
    </Stack>
  );
}

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
  const schedules = useApi<PartScheduleView[]>(['clash-parts', partId, 'schedules'], `/api/clash/parts/${partId}/schedules`);
  const [chosen, setChosen] = useState<string[] | null>(null);
  useEffect(() => {
    if (schedules.data && chosen === null) setChosen(schedules.data.filter((s) => s.selected).map((s) => s.id));
  }, [schedules.data, chosen]);

  async function approve() {
    setBusy(true);
    setProblems([]);
    try {
      if (dirty && onSave) await onSave();
      const result = await apiFetch<ApprovedVersionView>(`/api/clash/parts/${partId}/approve`, { method: 'POST', json: { comment, run, schedules: chosen ?? undefined } });
      notifications.show({ color: 'green', message: `Утверждена версия ${result.version}${result.runId ? ', прогон поставлен в очередь' : ''}` });
      await queryClient.invalidateQueries({ queryKey: ['clash-parts'] });
      await queryClient.invalidateQueries({ queryKey: ['clash-collisions'] });
      await queryClient.invalidateQueries({ queryKey: ['schedules'] });
      onClose();
    } catch (error) {
      const body = error instanceof ApiError ? (error.body as { errors?: string[] } | null) : null;
      setProblems(body?.errors?.length ? body.errors : [(error as Error).message]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal opened onClose={onClose} size="lg" title={`Утвердить версию ${nextVersion}`}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          Черновик станет утверждённой версией {nextVersion}. Расписания и коллизии в базе работают по утверждённой версии; черновик можно править дальше.
        </Text>
        {dirty && <Text size="sm" c="orange">Несохранённые правки будут сохранены перед утверждением.</Text>}
        <Divider />
        <ApprovePreviewBlock partId={partId} />
        {schedules.data && chosen && (
          <>
            <Divider />
            <ScheduleChoice schedules={schedules.data} value={chosen} onChange={setChosen} />
          </>
        )}
        <Divider />
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
