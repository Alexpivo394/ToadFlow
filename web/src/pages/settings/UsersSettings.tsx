import { useState } from 'react';
import { Button, Group, Modal, MultiSelect, PasswordInput, Select, Stack, Switch, Table, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { UserRole, UserView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
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

/** Заявка на регистрацию: координатор видит разделы из заявки, может их поправить, подтверждает или отклоняет. */
function PendingRequest({ user, known }: { user: UserView; known: string[] }) {
  const [disciplines, setDisciplines] = useState<string[]>(user.disciplines ?? []);
  const approve = useApiMutation<string[]>('POST', () => `/api/users/${user.id}/approve`, {
    invalidate: ['users'], success: `Заявка ${user.displayName} подтверждена`, json: (d) => ({ disciplines: d }),
  });
  const reject = useApiMutation('POST', () => `/api/users/${user.id}/reject`, { invalidate: ['users'], success: `Заявка ${user.displayName} отклонена` });
  const contacts = [user.position, user.email, user.phone].filter(Boolean).join(' · ');

  return (
    <Group data-request justify="space-between" align="flex-end" wrap="wrap" py="xs" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
      <Stack gap={0}>
        <Text fw={600} size="sm">{user.displayName}</Text>
        <Text size="xs" c="dimmed">{user.login}{contacts ? ` · ${contacts}` : ''} · {formatDateTime(user.createdAt)}</Text>
      </Stack>
      <Group gap="xs" align="flex-end">
        <MultiSelect size="xs" w={260} aria-label="Разделы" placeholder="Разделы" searchable comboboxProps={{ withinPortal: false }} data={[...new Set([...known, ...disciplines])]}
          value={disciplines} onChange={setDisciplines} />
        <Button size="xs" loading={approve.isPending} onClick={() => approve.mutate(disciplines)}>Подтвердить</Button>
        <ConfirmButton size="xs" variant="subtle" color="red" question={`Отклонить заявку ${user.displayName}? Учётная запись удалится.`}
          onConfirm={() => reject.mutate()}>
          Отклонить
        </ConfirmButton>
      </Group>
    </Group>
  );
}

/**
 * Пользователи. Координатор подтверждает заявки и задаёт разделы; роль, отключение, пароль, удаление и новые
 * учётные записи — только администратор.
 */
export function UsersSettings() {
  const { can } = useAuth();
  const admin = can('Admin');
  const users = useApi<UserView[]>(['users'], '/api/users');
  const pending = useApi<UserView[]>(['users', 'pending'], '/api/users/pending');
  const known = useApi<string[]>(['disciplines'], '/api/auth/disciplines');
  const [creating, setCreating] = useState(false);
  const [resetting, setResetting] = useState<UserView | null>(null);
  const update = useApiMutation<UserView>('PUT', (u) => `/api/users/${u.id}`, {
    invalidate: ['users'],
    json: (u) => ({ displayName: u.displayName, role: u.role, isDisabled: u.isDisabled }),
  });
  const setDisciplines = useApiMutation<{ id: string; disciplines: string[] }>('PUT', (u) => `/api/users/${u.id}/disciplines`, {
    invalidate: ['users'], json: (u) => u.disciplines,
  });
  const remove = useApiMutation<string>('DELETE', (id) => `/api/users/${id}`, { invalidate: ['users'], success: 'Пользователь удалён' });
  const list = (known.data ?? []);

  return (
    <Stack gap="md">
      {(pending.data ?? []).length > 0 && (
        <Panel title={`Заявки на регистрацию (${pending.data!.length})`}>
          {pending.data!.map((u) => <PendingRequest key={u.id} user={u} known={list} />)}
        </Panel>
      )}
      <Panel title="Пользователи" actions={admin && <Button size="xs" onClick={() => setCreating(true)}>+ Добавить</Button>}>
        <Table.ScrollContainer minWidth={800}>
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Кто</Table.Th>
                <Table.Th>Роль</Table.Th>
                <Table.Th>Разделы</Table.Th>
                <Table.Th>Последний вход</Table.Th>
                {admin && <Table.Th>Активен</Table.Th>}
                {admin && <Table.Th />}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {users.data?.filter((u) => !u.isPending).map((u) => (
                <Table.Tr key={u.id} style={{ opacity: u.isDisabled ? 0.55 : 1 }}>
                  <Table.Td>
                    <Text fw={600} size="sm">{u.displayName}</Text>
                    <Text size="xs" c="dimmed">{[u.login, u.position].filter(Boolean).join(' · ')}</Text>
                  </Table.Td>
                  <Table.Td>
                    {admin
                      ? <Select size="xs" w={170} aria-label="Роль" data={roles} value={u.role} allowDeselect={false} onChange={(v) => update.mutate({ ...u, role: v as UserRole })} />
                      : <Text size="sm">{t.role[u.role]}</Text>}
                  </Table.Td>
                  <Table.Td>
                    <MultiSelect size="xs" w={220} aria-label="Разделы" placeholder="—" searchable data={[...new Set([...list, ...(u.disciplines ?? [])])]}
                      value={u.disciplines ?? []} onChange={(v) => setDisciplines.mutate({ id: u.id, disciplines: v })} />
                  </Table.Td>
                  <Table.Td c="dimmed">{formatDateTime(u.lastLoginAt)}</Table.Td>
                  {admin && (
                    <Table.Td>
                      <Switch checked={!u.isDisabled} onChange={(e) => update.mutate({ ...u, isDisabled: !e.currentTarget.checked })} />
                    </Table.Td>
                  )}
                  {admin && (
                    <Table.Td>
                      <Group gap={4} justify="flex-end" wrap="nowrap">
                        <Button size="compact-xs" variant="default" onClick={() => setResetting(u)}>Сбросить пароль</Button>
                        <ConfirmButton size="compact-xs" variant="subtle" question={`Удалить пользователя ${u.displayName}?`} onConfirm={() => remove.mutate(u.id)}>
                          {t.common.delete}
                        </ConfirmButton>
                      </Group>
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
        <Text size="xs" c="dimmed" mt="sm">
          <b>Администратор</b> — всё. <b>Координатор</b> — расписания, запуски, настройка проверок коллизий, заявки и разделы.{' '}
          <b>Проектировщик</b> — свои коллизии. <b>Наблюдатель</b> — только смотрит.
        </Text>
        {creating && <NewUserModal onClose={() => setCreating(false)} />}
        {resetting && <PasswordModal user={resetting} onClose={() => setResetting(null)} />}
      </Panel>
    </Stack>
  );
}
