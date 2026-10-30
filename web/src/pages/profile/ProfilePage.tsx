import { useEffect, useState } from 'react';
import { Badge, Button, Group, PasswordInput, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { apiFetch } from '../../api/client';
import { useApi } from '../../api/queries';
import type { UserView } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { PageTitle, Panel } from '../../components/ui';
import { t } from '../../i18n/ru';

const orNull = (value: string) => value.trim() || null;

/** Личный кабинет: имя и контакты (их видят коллеги в ленте коллизии), роль и разделы — только чтение, смена пароля. */
export function ProfilePage() {
  const { refresh } = useAuth();
  const profile = useApi<UserView>(['profile'], '/api/profile');
  const [form, setForm] = useState({ displayName: '', position: '', email: '', phone: '' });
  const [password, setPassword] = useState({ current: '', new: '' });
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const p = profile.data;
    if (p) setForm({ displayName: p.displayName, position: p.position ?? '', email: p.email ?? '', phone: p.phone ?? '' });
  }, [profile.data]);

  async function run(action: () => Promise<unknown>, success: string) {
    setBusy(true);
    try {
      await action();
      notifications.show({ color: 'green', message: success });
      return true;
    } catch (error) {
      notifications.show({ color: 'red', title: 'Не получилось', message: (error as Error).message });
      return false;
    } finally {
      setBusy(false);
    }
  }

  const p = profile.data;
  if (!p) return null;
  const disciplines = p.disciplines ?? [];

  return (
    <>
      <PageTitle>{t.nav.profile}</PageTitle>
      <Stack gap="md" maw={560}>
        <Panel title="О себе">
          <Stack gap="sm">
            <TextInput label="Имя" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.currentTarget.value })} />
            <Text size="sm">Логин: <b>{p.login}</b> · роль: <b>{t.role[p.role]}</b></Text>
            <TextInput label="Должность" value={form.position} onChange={(e) => setForm({ ...form, position: e.currentTarget.value })} />
            <TextInput label="Почта" value={form.email} onChange={(e) => setForm({ ...form, email: e.currentTarget.value })} />
            <TextInput label="Телефон" value={form.phone} onChange={(e) => setForm({ ...form, phone: e.currentTarget.value })} />
            <Stack gap={4}>
              <Text size="sm" fw={500}>Разделы</Text>
              <Group gap={4}>
                {disciplines.length > 0 ? disciplines.map((d) => <Badge key={d} variant="light">{d}</Badge>) : <Text size="sm" c="dimmed">не указаны</Text>}
              </Group>
              <Text size="xs" c="dimmed">Разделы меняет координатор или администратор.</Text>
            </Stack>
            <Group justify="flex-end">
              <Button loading={busy} disabled={!form.displayName.trim()}
                onClick={() => run(async () => {
                  await apiFetch('/api/profile', {
                    method: 'PUT',
                    json: { displayName: form.displayName.trim(), position: orNull(form.position), email: orNull(form.email), phone: orNull(form.phone) },
                  });
                  await profile.refetch();
                  await refresh();
                }, 'Профиль сохранён')}>
                {t.common.save}
              </Button>
            </Group>
          </Stack>
        </Panel>
        <Panel title="Пароль">
          <Stack gap="sm">
            <PasswordInput label="Текущий пароль" value={password.current} onChange={(e) => setPassword({ ...password, current: e.currentTarget.value })} />
            <PasswordInput label="Новый пароль" description="Не короче 6 символов" value={password.new}
              onChange={(e) => setPassword({ ...password, new: e.currentTarget.value })} />
            <Group justify="flex-end">
              <Button variant="default" loading={busy} disabled={!password.current || !password.new}
                onClick={async () => {
                  if (await run(() => apiFetch('/api/profile/password', { method: 'POST', json: password }), 'Пароль изменён'))
                    setPassword({ current: '', new: '' });
                }}>
                Сменить пароль
              </Button>
            </Group>
          </Stack>
        </Panel>
      </Stack>
    </>
  );
}
