import { useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { Alert, Anchor, Button, Center, MultiSelect, Paper, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { apiFetch } from '../api/client';
import { useApi } from '../api/queries';
import { Brand } from '../components/ui';

/** Самостоятельная регистрация проектировщика: заявку подтверждает координатор, до этого войти нельзя. */
export function RegisterPage() {
  const disciplines = useApi<string[]>(['disciplines'], '/api/auth/disciplines');
  const [form, setForm] = useState({ displayName: '', login: '', password: '', disciplines: [] as string[], position: '', email: '', phone: '' });
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await apiFetch('/api/auth/register', {
        method: 'POST',
        json: { ...form, position: form.position.trim() || null, email: form.email.trim() || null, phone: form.phone.trim() || null },
      });
      setSent(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Center mih="100vh" p="md">
      <Paper withBorder shadow="sm" p="xl" radius="md" w="100%" maw={440}>
        {sent ? (
          <Stack>
            <Title order={3}>Заявка отправлена</Title>
            <Text>Войти можно после того, как координатор подтвердит заявку.</Text>
            <Anchor component={RouterLink} to="/">Ко входу</Anchor>
          </Stack>
        ) : (
          <form onSubmit={submit}>
            <Stack>
              <Brand size={40}><Title order={2}>Регистрация</Title></Brand>
              {error && <Alert color="red">{error}</Alert>}
              <TextInput label="Имя и фамилия" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.currentTarget.value })} required />
              <TextInput label="Логин" value={form.login} onChange={(e) => setForm({ ...form, login: e.currentTarget.value })} required />
              <PasswordInput label="Пароль" description="Не короче 6 символов" value={form.password}
                onChange={(e) => setForm({ ...form, password: e.currentTarget.value })} required />
              <MultiSelect label="Разделы" description="Какие разделы вы ведёте — по ним вы увидите свои коллизии" searchable
                data={disciplines.data ?? []} value={form.disciplines} onChange={(v) => setForm({ ...form, disciplines: v })} />
              <TextInput label="Должность" value={form.position} onChange={(e) => setForm({ ...form, position: e.currentTarget.value })} />
              <TextInput label="Почта" value={form.email} onChange={(e) => setForm({ ...form, email: e.currentTarget.value })} />
              <TextInput label="Телефон" value={form.phone} onChange={(e) => setForm({ ...form, phone: e.currentTarget.value })} />
              <Button type="submit" loading={busy}>Отправить заявку</Button>
              <Anchor component={RouterLink} to="/" size="sm" ta="center">Уже есть учётная запись — войти</Anchor>
            </Stack>
          </form>
        )}
      </Paper>
    </Center>
  );
}
