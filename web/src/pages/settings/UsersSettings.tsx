import { useState } from 'react';
import { Button, Group, Modal, PasswordInput, Select, Stack, Switch, Table, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { UserRole, UserView } from '../../api/types';
import { ConfirmButton, Panel } from '../../components/ui';
import { t } from '../../i18n/ru';
import { formatDateTime } from '../../lib/format';

const roles = (Object.keys(t.role) as UserRole[]).map((value) => ({ value, label: t.role[value] }));

function NewUserModal({ onClose }: { onClose: () => void }) {
  const [draft, setDraft] = useState({ login: '', displayName: '', role: 'Operator' as UserRole, password: '' });
  const create = useApiMutation<typeof draft>('POST', () => '/api/users', { invalidate: ['users'], success: 'Пользователь создан' });

  return (
    <Modal opened onClose={onClose} title="Новый пользователь">
      <Stack>
        <TextInput label="Имя" value={draft.displayName} onChange={(e) => setDraft({ ...draft, displayName: e.currentTarget.value })} />
        <TextInput label="Логин" value={draft.login} onChange={(e) => setDraft({ ...draft, login: e.currentTarget.value })} />
        <Select label="Роль" data={roles} value={draft.role} allowDeselect={false} onChange={(v) => setDraft({ ...draft, role: v as UserRole })} />
        <PasswordInput label="Пароль" description="Не короче 6 символов" value={draft.password} onChange={(e) => setDraft({ ...draft, password: e.currentTarget.value })} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t.common.cancel}</Button>
          <Button loading={create.isPending} onClick={() => create.mutate(draft, { onSuccess: onClose })}>{t.common.create}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function PasswordModal({ user, onClose }: { user: UserView; onClose: () => void }) {
  const [password, setPassword] = useState('');
  const reset = useApiMutation<string>('POST', () => `/api/users/${user.id}/password`, { json: (p) => ({ password: p }), success: 'Пароль изменён' });

  return (
    <Modal opened onClose={onClose} title={`Новый пароль для ${user.displayName}`}>
      <Stack>
        <PasswordInput label="Пароль" value={password} onChange={(e) => setPassword(e.currentTarget.value)} />
        <Group justify="flex-end">
          <Button loading={reset.isPending} onClick={() => reset.mutate(password, { onSuccess: onClose })}>{t.common.save}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export function UsersSettings() {
  const users = useApi<UserView[]>(['users'], '/api/users');
  const [creating, setCreating] = useState(false);
  const [resetting, setResetting] = useState<UserView | null>(null);
  const update = useApiMutation<UserView>('PUT', (u) => `/api/users/${u.id}`, {
    invalidate: ['users'],
    json: (u) => ({ displayName: u.displayName, role: u.role, isDisabled: u.isDisabled }),
  });
  const remove = useApiMutation<string>('DELETE', (id) => `/api/users/${id}`, { invalidate: ['users'], success: 'Пользователь удалён' });

  return (
    <Panel title="Пользователи" actions={<Button size="xs" onClick={() => setCreating(true)}>+ Добавить</Button>}>
      <Table.ScrollContainer minWidth={700}>
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Кто</Table.Th>
              <Table.Th>Роль</Table.Th>
              <Table.Th>Последний вход</Table.Th>
              <Table.Th>Активен</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {users.data?.map((u) => (
              <Table.Tr key={u.id} style={{ opacity: u.isDisabled ? 0.55 : 1 }}>
                <Table.Td>
                  <Text fw={600} size="sm">{u.displayName}</Text>
                  <Text size="xs" c="dimmed">{u.login}</Text>
                </Table.Td>
                <Table.Td>
                  <Select size="xs" w={170} data={roles} value={u.role} allowDeselect={false} onChange={(v) => update.mutate({ ...u, role: v as UserRole })} />
                </Table.Td>
                <Table.Td c="dimmed">{formatDateTime(u.lastLoginAt)}</Table.Td>
                <Table.Td>
                  <Switch checked={!u.isDisabled} onChange={(e) => update.mutate({ ...u, isDisabled: !e.currentTarget.checked })} />
                </Table.Td>
                <Table.Td>
                  <Group gap={4} justify="flex-end" wrap="nowrap">
                    <Button size="compact-xs" variant="default" onClick={() => setResetting(u)}>Сбросить пароль</Button>
                    <ConfirmButton size="compact-xs" variant="subtle" question={`Удалить пользователя ${u.displayName}?`} onConfirm={() => remove.mutate(u.id)}>
                      {t.common.delete}
                    </ConfirmButton>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      <Text size="xs" c="dimmed" mt="sm">
        <b>Администратор</b> — всё. <b>Оператор</b> — расписания, пресеты, запуски, «Убить» Revit. <b>Наблюдатель</b> — только смотрит.
      </Text>
      {creating && <NewUserModal onClose={() => setCreating(false)} />}
      {resetting && <PasswordModal user={resetting} onClose={() => setResetting(null)} />}
    </Panel>
  );
}
