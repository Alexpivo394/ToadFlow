import { useState } from 'react';
import { Alert, Button, Drawer, Group, Modal, MultiSelect, PasswordInput, Select, Stack, Switch, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '../../api/client';
import { useApi, useApiMutation } from '../../api/queries';
import type { UserRole, UserView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { DataTable, type Column } from '../../components/DataTable';
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

type BulkAction = 'AddDiscipline' | 'RemoveDiscipline' | 'Disable' | 'Enable';

/** Действия над выбранными: разделы — координатор, отключить и включить — администратор. Кого сервер пропустил — показываем. */
function BulkBar({ selected, known, onDone }: { selected: string[]; known: string[]; onDone: () => void }) {
  const { can } = useAuth();
  const queryClient = useQueryClient();
  const [choosing, setChoosing] = useState<'AddDiscipline' | 'RemoveDiscipline' | null>(null);
  const [discipline, setDiscipline] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [skipped, setSkipped] = useState<string[]>([]);

  async function run(action: BulkAction, name: string | null = null) {
    setBusy(true);
    try {
      const result = await apiFetch<{ changed: number; skipped: string[] }>('/api/users/bulk', {
        method: 'POST', json: { ids: selected, action, discipline: name },
      });
      setSkipped(result.skipped);
      notifications.show({ color: 'green', message: `Изменено пользователей: ${result.changed}` });
      setChoosing(null);
      setDiscipline(null);
      onDone();
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
    } finally {
      setBusy(false);
      await queryClient.invalidateQueries({ queryKey: ['users'] });
    }
  }

  return (
    <Stack gap="xs">
      {selected.length > 0 && (
        <Group gap="xs">
          <Text size="sm">Выбрано: {selected.length}</Text>
          <Button size="xs" variant="default" onClick={() => setChoosing('AddDiscipline')}>Добавить раздел</Button>
          <Button size="xs" variant="default" onClick={() => setChoosing('RemoveDiscipline')}>Убрать раздел</Button>
          {can('Admin') && (
            <>
              <Button size="xs" variant="default" loading={busy} onClick={() => void run('Disable')}>Отключить</Button>
              <Button size="xs" variant="default" loading={busy} onClick={() => void run('Enable')}>Включить</Button>
            </>
          )}
        </Group>
      )}
      {skipped.length > 0 && (
        <Alert color="yellow" withCloseButton onClose={() => setSkipped([])} title="Не изменены">
          {skipped.map((line) => <Text key={line} size="sm">{line}</Text>)}
        </Alert>
      )}
      {choosing && (
        <Modal opened onClose={() => setChoosing(null)} title={choosing === 'AddDiscipline' ? 'Добавить раздел выбранным' : 'Убрать раздел у выбранных'}>
          <Stack>
            <Select label="Раздел" data={known} value={discipline} onChange={setDiscipline} searchable defaultDropdownOpened
              comboboxProps={{ withinPortal: false }} />
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setChoosing(null)}>{t.common.cancel}</Button>
              <Button disabled={!discipline} loading={busy} onClick={() => void run(choosing, discipline)}>Применить</Button>
            </Group>
          </Stack>
        </Modal>
      )}
    </Stack>
  );
}

/** Пользователи. Координатор подтверждает заявки; по щелчку в списке — карточка пользователя. Новые учётные записи — администратор. */
export function UsersPage() {
  const { can } = useAuth();
  const admin = can('Admin');
  const users = useApi<UserView[]>(['users'], '/api/users');
  const pending = useApi<UserView[]>(['users', 'pending'], '/api/users/pending');
  const known = useApi<string[]>(['disciplines'], '/api/auth/disciplines');
  const [creating, setCreating] = useState(false);
  const [opened, setOpened] = useState<UserView | null>(null);
  const [resetting, setResetting] = useState<UserView | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const list = known.data ?? [];
  const rows = (users.data ?? []).filter((u) => !u.isPending);

  const columns: Column<UserView>[] = [
    {
      key: 'name', title: 'Кто', sort: (u) => u.displayName,
      render: (u) => (
        <>
          <Text fw={600} size="sm">{u.displayName}</Text>
          <Text size="xs" c="dimmed">{[u.login, u.position].filter(Boolean).join(' · ')}</Text>
        </>
      ),
    },
    {
      key: 'role', title: 'Роль', sort: (u) => t.role[u.role], render: (u) => <Text size="sm">{t.role[u.role]}</Text>,
      filter: { options: roles, match: (u, values) => values.includes(u.role) },
    },
    {
      key: 'disciplines', title: 'Разделы', sort: (u) => (u.disciplines ?? []).join(', ') || null,
      render: (u) => <Text size="sm">{(u.disciplines ?? []).join(', ') || '—'}</Text>,
      filter: {
        options: [...new Set([...list, ...rows.flatMap((u) => u.disciplines ?? [])])].map((d) => ({ value: d, label: d })),
        match: (u, values) => (u.disciplines ?? []).some((d) => values.includes(d)),
      },
    },
    {
      key: 'state', title: 'Состояние', sort: (u) => (u.isDisabled ? 1 : 0),
      render: (u) => <Text size="sm" c={u.isDisabled ? 'dimmed' : undefined}>{u.isDisabled ? 'отключён' : 'активен'}</Text>,
      filter: {
        options: [{ value: 'active', label: 'активен' }, { value: 'disabled', label: 'отключён' }],
        match: (u, values) => values.includes(u.isDisabled ? 'disabled' : 'active'),
      },
    },
    { key: 'last', title: 'Последний вход', sort: (u) => u.lastLoginAt, render: (u) => <Text size="sm" c="dimmed">{formatDateTime(u.lastLoginAt)}</Text> },
    { key: 'created', title: 'Создан', sort: (u) => u.createdAt ?? null, render: (u) => <Text size="sm" c="dimmed">{formatDateTime(u.createdAt ?? null)}</Text> },
  ];

  return (
    <Stack gap="md">
      {(pending.data ?? []).length > 0 && (
        <Panel title={`Заявки на регистрацию (${pending.data!.length})`}>
          {pending.data!.map((u) => <PendingRequest key={u.id} user={u} known={list} />)}
        </Panel>
      )}
      <Panel title="Пользователи" actions={admin && <Button size="xs" onClick={() => setCreating(true)}>+ Добавить</Button>}>
        <Stack gap="sm">
          <BulkBar selected={selected} known={list} onDone={() => setSelected([])} />
          <DataTable rows={rows} columns={columns} rowKey={(u) => u.id} search={(u) => [u.displayName, u.login, u.position ?? ''].join(' ')}
            searchPlaceholder="имя, логин, должность…" selection={{ selected, onChange: setSelected }} onRowClick={setOpened}
            rowStyle={(u) => (u.isDisabled ? { opacity: 0.55 } : undefined)} empty="Пользователей нет" />
        </Stack>
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

