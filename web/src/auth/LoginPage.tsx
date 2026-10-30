import { useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { Alert, Anchor, Button, Center, Paper, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { ApiError } from '../api/client';
import { Brand } from '../components/ui';
import { t } from '../i18n/ru';
import { useAuth } from './AuthContext';

export function LoginPage() {
  const { login } = useAuth();
  const [loginName, setLoginName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(loginName, password);
    } catch (e) {
      // 403 — заявка ждёт подтверждения: сервер говорит, почему; остальное — неверный логин или пароль.
      setError(e instanceof ApiError && e.status === 403 ? e.message : t.auth.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Center mih="100vh" p="md">
      <Paper withBorder shadow="sm" p="xl" radius="md" w="100%" maw={380}>
        <form onSubmit={submit}>
          <Stack>
            <Brand size={40}><Title order={2}>{t.auth.title}</Title></Brand>
            {error && <Alert color="red">{error}</Alert>}
            <TextInput label={t.auth.login} value={loginName} onChange={(e) => setLoginName(e.currentTarget.value)} autoFocus required />
            <PasswordInput label={t.auth.password} value={password} onChange={(e) => setPassword(e.currentTarget.value)} required />
            <Button type="submit" loading={busy}>
              {t.auth.submit}
            </Button>
            <Text size="sm" ta="center">
              Нет учётной записи? <Anchor component={RouterLink} to="/register">Регистрация</Anchor>
            </Text>
          </Stack>
        </form>
      </Paper>
    </Center>
  );
}
