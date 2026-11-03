import { useState } from 'react';
import { Link as RouterLink } from 'react-router';
import { Alert, Anchor, Button, Center, Checkbox, Paper, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { ApiError } from '../api/client';
import { Brand } from '../components/ui';
import { t } from '../i18n/ru';
import { useAuth } from './AuthContext';

type Problem = { kind: 'error' | 'info'; text: string };

export function LoginPage() {
  const { login } = useAuth();
  const [loginName, setLoginName] = useState('');
  const [password, setPassword] = useState('');
  const [remember, setRemember] = useState(true);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setProblem(null);
    try {
      await login(loginName, password, remember);
    } catch (e) {
      // 403 — заявка ждёт подтверждения: это не ошибка человека, а ожидание; 429 — слишком много попыток; остальное — логин или пароль.
      if (e instanceof ApiError && e.status === 403) setProblem({ kind: 'info', text: e.message });
      else if (e instanceof ApiError && e.status === 429) setProblem({ kind: 'error', text: e.message });
      else setProblem({ kind: 'error', text: t.auth.failed });
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
            {problem && <Alert color={problem.kind === 'info' ? 'blue' : 'red'}>{problem.text}</Alert>}
            <TextInput label={t.auth.login} value={loginName} onChange={(e) => setLoginName(e.currentTarget.value)} autoFocus required />
            <PasswordInput label={t.auth.password} value={password} onChange={(e) => setPassword(e.currentTarget.value)} required />
            <Checkbox label="Оставаться в системе" description="Не ставьте на чужом или общем компьютере" checked={remember}
              onChange={(e) => setRemember(e.currentTarget.checked)} />
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
