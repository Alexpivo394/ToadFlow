import { useState, type ReactNode } from 'react';
import { Autocomplete, Button, Group, Modal, Stack, Text, Textarea, TextInput, Tooltip, type ButtonProps } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import { askConfirm } from '../../components/ui';
import { t } from '../../i18n/ru';
import { familiesKey, type FileActionsState, type IssueAnswer } from './api';

/**
 * Можно ли сейчас менять файлы библиотеки кнопками: агент библиотеки новой версии в сети. Если нет — кнопки неактивны,
 * а подсказка говорит почему.
 */
export function useFileActions() {
  const state = useApi<FileActionsState>([familiesKey, 'actions'], '/api/families/actions');
  return { available: state.data?.available ?? false, reason: state.data?.reason ?? null };
}

/** Действие с файлом: POST без тела или с телом; после — всё по семействам перечитывается. */
export function useFamilyAction<TBody = void>(path: (body: TBody) => string, success: string, json?: (body: TBody) => unknown) {
  return useApiMutation<TBody>('POST', path, { invalidate: [familiesKey], success, json: json ?? (() => undefined) });
}

/** Кнопка действия с файлом: неактивна с подсказкой, пока агент не может; спрашивает подтверждение, если задан вопрос. */
export function FileActionButton({ question, onRun, loading, children, ...props }: ButtonProps & {
  question?: string;
  onRun: () => void;
  loading?: boolean;
  children: ReactNode;
}) {
  const { available, reason } = useFileActions();
  const button = (
    <Button size="compact-xs" variant="default" disabled={!available} loading={loading} {...props}
      onClick={(event) => {
        event.stopPropagation();
        if (question) askConfirm(question, onRun, props.color ?? 'blue');
        else onRun();
      }}>
      {children}
    </Button>
  );
  // Обёртка одна и та же в обоих состояниях: кнопка не пересоздаётся, когда ответ о доступности приходит.
  return (
    <Tooltip disabled={available} label={reason ? `${t.families.actionsUnavailable}: ${reason}` : t.families.actionsUnavailable}>
      <span>{button}</span>
    </Tooltip>
  );
}

/** «Утвердить и переложить в…»: папка из тех, где уже есть утверждённые семейства, или новая. */
export function ApproveNewButton({ cardId, name }: { cardId: string; name: string }) {
  const [opened, setOpened] = useState(false);
  const [folder, setFolder] = useState('');
  const folders = useApi<string[]>([familiesKey, 'folders'], opened ? '/api/families/folders' : null);
  const approve = useFamilyAction<string>(() => `/api/families/${cardId}/approve`, t.families.approved, (value) => ({ folder: value }));
  return (
    <>
      <FileActionButton color="teal" onRun={() => setOpened(true)}>{t.families.approveInto}</FileActionButton>
      <Modal opened={opened} onClose={() => setOpened(false)} title={`${t.families.approveInto} — ${name}`} onClick={(e) => e.stopPropagation()}>
        <Stack>
          <Autocomplete label={t.families.folder} description={t.families.folderHint} data={folders.data ?? []} value={folder} onChange={setFolder}
            placeholder="ОВ\Клапаны" data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button color="teal" disabled={folder.trim() === ''} loading={approve.isPending}
              onClick={() => approve.mutate(folder.trim(), { onSuccess: () => setOpened(false) })}>{t.families.approve}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** «Отклонить…»: с причиной; файл уходит из папки проверки, версия остаётся в истории с пометкой. */
export function RejectButton({ versionId, name }: { versionId: string; name: string }) {
  const [opened, setOpened] = useState(false);
  const [reason, setReason] = useState('');
  const reject = useFamilyAction<string>(() => `/api/families/versions/${versionId}/reject`, t.families.rejected, (value) => ({ reason: value }));
  return (
    <>
      <FileActionButton color="red" onRun={() => setOpened(true)}>{t.families.reject}</FileActionButton>
      <Modal opened={opened} onClose={() => setOpened(false)} title={`${t.families.reject} — ${name}`} onClick={(e) => e.stopPropagation()}>
        <Stack>
          <Text size="sm" c="dimmed">{t.families.rejectHint}</Text>
          <Textarea label={t.families.reason} autosize minRows={2} value={reason} onChange={(e) => setReason(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button color="red" disabled={reason.trim() === ''} loading={reject.isPending}
              onClick={() => reject.mutate(reason.trim(), { onSuccess: () => setOpened(false) })}>{t.families.reject}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Ответ на случай «Нужно решение». */
export function useIssueAnswer() {
  return useApiMutation<{ id: string; answer: IssueAnswer; name?: string }>('POST', (body) => `/api/families/issues/${body.id}/answer`, {
    invalidate: [familiesKey],
    success: t.families.answered,
    json: (body) => ({ answer: body.answer, name: body.name ?? null }),
  });
}

/** «Переименовать…» при конфликте имён: новое имя файла без расширения. */
export function RenameButton({ issueId, current }: { issueId: string; current: string }) {
  const [opened, setOpened] = useState(false);
  const [name, setName] = useState(current);
  const answer = useIssueAnswer();
  return (
    <>
      <FileActionButton onRun={() => setOpened(true)}>{t.families.rename}</FileActionButton>
      <Modal opened={opened} onClose={() => setOpened(false)} title={t.families.rename} onClick={(e) => e.stopPropagation()}>
        <Stack>
          <TextInput label={t.families.newName} description={t.families.newNameHint} value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>{t.common.cancel}</Button>
            <Button disabled={name.trim() === '' || name.trim() === current} loading={answer.isPending}
              onClick={() => answer.mutate({ id: issueId, answer: 'rename', name: name.trim() }, { onSuccess: () => setOpened(false) })}>{t.families.rename}</Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
