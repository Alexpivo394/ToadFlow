import { useState } from 'react';
import { Alert, Button, Center, Paper, PasswordInput, Stack, TextInput, Title } from '@mantine/core';
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
    } catch {
      setError(t.auth.failed);
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
          </Stack>
        </form>
      </Paper>
    </Center>
  );
}
