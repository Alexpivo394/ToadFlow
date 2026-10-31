import { useState } from 'react';
import { Button, Drawer, Group, Modal, MultiSelect, PasswordInput, Select, Stack, Switch, Table, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
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

const orNull = (s: string) => s.trim() || null;

/**
 * Карточка пользователя по щелчку в списке: имя, контакты и разделы правит координатор; роль, «Активен», пароль и
 * удаление — администратор. «Сохранить» отправляет только изменённое.
 */
function UserCard({ user, known, onClose, onResetPassword }: { user: UserView; known: string[]; onClose: () => void; onResetPassword: () => void }) {
  const { can } = useAuth();
  const admin = can('Admin');
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState({
    displayName: user.displayName, position: user.position ?? '', email: user.email ?? '', phone: user.phone ?? '',
    disciplines: user.disciplines ?? [], role: user.role, isDisabled: user.isDisabled,
  });
  const [saving, setSaving] = useState(false);
  const remove = useApiMutation('DELETE', () => `/api/users/${user.id}`, { invalidate: ['users'], success: 'Пользователь удалён' });

  async function save() {
    const contacts = { displayName: draft.displayName.trim(), position: orNull(draft.position), email: orNull(draft.email), phone: orNull(draft.phone) };
    const contactsChanged = contacts.displayName !== user.displayName || contacts.position !== (user.position ?? null)
      || contacts.email !== (user.email ?? null) || contacts.phone !== (user.phone ?? null);
    const disciplinesChanged = draft.disciplines.join('\n') !== (user.disciplines ?? []).join('\n');
    const accessChanged = admin && (draft.role !== user.role || draft.isDisabled !== user.isDisabled);
    setSaving(true);
    try {
      if (contactsChanged) await apiFetch(`/api/users/${user.id}/contacts`, { method: 'PUT', json: contacts });
      if (disciplinesChanged) await apiFetch(`/api/users/${user.id}/disciplines`, { method: 'PUT', json: draft.disciplines });
      if (accessChanged)
        await apiFetch(`/api/users/${user.id}`, { method: 'PUT', json: { displayName: contacts.displayName, role: draft.role, isDisabled: draft.isDisabled } });
      notifications.show({ color: 'green', message: `${contacts.displayName}: сохранено` });
      onClose();
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    } finally {
      setSaving(false);
      await queryClient.invalidateQueries({ queryKey: ['users'] });
    }
  }

  return (
    <Drawer opened onClose={onClose} position="right" size="md" title={user.displayName}>
      <Stack gap="sm">
        <Text size="xs" c="dimmed">
          {user.login} · {t.role[user.role]} · с {formatDateTime(user.createdAt)} · последний вход {formatDateTime(user.lastLoginAt)}
        </Text>
        <TextInput label="Имя" value={draft.displayName} onChange={(e) => setDraft({ ...draft, displayName: e.currentTarget.value })} />
        <TextInput label="Должность" value={draft.position} onChange={(e) => setDraft({ ...draft, position: e.currentTarget.value })} />
        <TextInput label="Почта" value={draft.email} onChange={(e) => setDraft({ ...draft, email: e.currentTarget.value })} />
        <TextInput label="Телефон" value={draft.phone} onChange={(e) => setDraft({ ...draft, phone: e.currentTarget.value })} />
        <MultiSelect label="Разделы" placeholder="—" searchable data={[...new Set([...known, ...draft.disciplines])]} value={draft.disciplines}
          onChange={(disciplines) => setDraft({ ...draft, disciplines })} />
        {admin && (
          <>
            <Select label="Роль" data={roles} value={draft.role} allowDeselect={false} onChange={(v) => setDraft({ ...draft, role: v as UserRole })} />
            <Switch label="Активен" checked={!draft.isDisabled} onChange={(e) => setDraft({ ...draft, isDisabled: !e.currentTarget.checked })} />
          </>
        )}
        <Group justify="space-between" mt="sm">
          <Group gap="xs">
            {admin && <Button size="xs" variant="default" onClick={onResetPassword}>Сбросить пароль</Button>}
            {admin && (
              <ConfirmButton size="xs" variant="subtle" color="red" question={`Удалить пользователя ${user.displayName}?`}
                onConfirm={() => remove.mutate(undefined, { onSuccess: onClose })}>
                {t.common.delete}
              </ConfirmButton>
            )}
          </Group>
          <Button loading={saving} disabled={!draft.displayName.trim()} onClick={() => void save()}>{t.common.save}</Button>
        </Group>
      </Stack>
    </Drawer>
  );
}

/** Пользователи. Координатор подтверждает заявки; по щелчку в списке — карточка пользователя. Новые учётные записи — администратор. */
export function UsersSettings() {
  const { can } = useAuth();
  const admin = can('Admin');
  const users = useApi<UserView[]>(['users'], '/api/users');
  const pending = useApi<UserView[]>(['users', 'pending'], '/api/users/pending');
  const known = useApi<string[]>(['disciplines'], '/api/auth/disciplines');
  const [creating, setCreating] = useState(false);
  const [opened, setOpened] = useState<UserView | null>(null);
  const [resetting, setResetting] = useState<UserView | null>(null);
  const list = known.data ?? [];

  return (
    <Stack gap="md">
      {(pending.data ?? []).length > 0 && (
        <Panel title={`Заявки на регистрацию (${pending.data!.length})`}>
          {pending.data!.map((u) => <PendingRequest key={u.id} user={u} known={list} />)}
        </Panel>
      )}
      <Panel title="Пользователи" actions={admin && <Button size="xs" onClick={() => setCreating(true)}>+ Добавить</Button>}>
        <Table.ScrollContainer minWidth={700}>
          <Table highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Кто</Table.Th>
                <Table.Th>Роль</Table.Th>
                <Table.Th>Разделы</Table.Th>
                <Table.Th>Последний вход</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {users.data?.filter((u) => !u.isPending).map((u) => (
                <Table.Tr key={u.id} style={{ cursor: 'pointer', opacity: u.isDisabled ? 0.55 : 1 }} onClick={() => setOpened(u)}>
                  <Table.Td>
                    <Text fw={600} size="sm">{u.displayName}</Text>
                    <Text size="xs" c="dimmed">{[u.login, u.position, u.isDisabled ? 'отключён' : null].filter(Boolean).join(' · ')}</Text>
                  </Table.Td>
                  <Table.Td><Text size="sm">{t.role[u.role]}</Text></Table.Td>
                  <Table.Td><Text size="sm">{(u.disciplines ?? []).join(', ') || '—'}</Text></Table.Td>
                  <Table.Td c="dimmed">{formatDateTime(u.lastLoginAt)}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
        <Text size="xs" c="dimmed" mt="sm">
          Щёлкните по пользователю — откроется карточка. <b>Администратор</b> — всё. <b>Координатор</b> — расписания, запуски, настройка проверок
          коллизий, заявки, разделы и контакты. <b>Проектировщик</b> — свои коллизии. <b>Наблюдатель</b> — только смотрит.
        </Text>
        {creating && <NewUserModal onClose={() => setCreating(false)} />}
        {opened && <UserCard key={opened.id} user={opened} known={list} onClose={() => setOpened(null)} onResetPassword={() => setResetting(opened)} />}
        {resetting && <PasswordModal user={resetting} onClose={() => setResetting(null)} />}
      </Panel>
    </Stack>
  );
}

