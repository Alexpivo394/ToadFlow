import { useState } from 'react';
import { Alert, Button, Code, CopyButton, Group, Stack, Table, Text, TextInput } from '@mantine/core';
import { useApi, useApiMutation } from '../../api/queries';
import type { ApiToken } from '../../api/types';
import { ConfirmButton, Empty, Panel } from '../../components/ui';
import { formatDateTime } from '../../lib/format';

/**
 * Личные токены доступа: программа (например, плагин ParamChecker) входит ими от имени пользователя и с его правами.
 * Сам токен показывается один раз, при создании.
 */
export function TokensSettings() {
  const tokens = useApi<ApiToken[]>(['tokens'], '/api/auth/tokens');
  const [name, setName] = useState('');
  const [created, setCreated] = useState<string | null>(null);
  const create = useApiMutation<string, { token: string }>('POST', () => '/api/auth/tokens', { invalidate: ['tokens'], json: (n) => ({ name: n }) });
  const revoke = useApiMutation<string>('DELETE', (id) => `/api/auth/tokens/${id}`, { invalidate: ['tokens'], success: 'Токен отозван' });

  return (
    <Stack>
      <Panel title="Токены доступа">
        <Text size="sm" c="dimmed" mb="sm">
          Токен нужен программам, которые работают с ToadFlow от вашего имени, — например, плагину ParamChecker, чтобы ручные проверки попадали в
          аналитику. Токен даёт те же права, что у вас; храните его как пароль. Ненужный — отзовите.
        </Text>
        {created && (
          <Alert color="green" mb="sm" title="Токен создан — скопируйте его сейчас, больше он показан не будет" withCloseButton onClose={() => setCreated(null)}>
            <Group gap="xs" wrap="nowrap">
              <Code style={{ wordBreak: 'break-all' }}>{created}</Code>
              <CopyButton value={created}>
                {({ copied, copy }) => <Button size="compact-sm" variant="default" onClick={copy}>{copied ? 'Скопирован' : 'Скопировать'}</Button>}
              </CopyButton>
            </Group>
          </Alert>
        )}
        <Group align="flex-end" mb="md">
          <TextInput label="Для чего" placeholder="ParamChecker на рабочем компьютере" value={name} w={360} onChange={(e) => setName(e.currentTarget.value)} />
          <Button disabled={!name.trim()} loading={create.isPending}
            onClick={() => create.mutate(name.trim(), { onSuccess: (result) => { setCreated(result.token); setName(''); } })}>
            Создать токен
          </Button>
        </Group>
        {tokens.data && tokens.data.length === 0 ? (
          <Empty>Токенов нет</Empty>
        ) : (
          <Table>
            <Table.Tbody>
              {tokens.data?.map((token) => (
                <Table.Tr key={token.id}>
                  <Table.Td fw={600}>{token.name}</Table.Td>
                  <Table.Td><Code>{token.prefix}…</Code></Table.Td>
                  <Table.Td c="dimmed">создан {formatDateTime(token.createdAt)}</Table.Td>
                  <Table.Td c="dimmed">{token.lastUsedAt ? `использован ${formatDateTime(token.lastUsedAt)}` : 'ещё не использовался'}</Table.Td>
                  <Table.Td>
                    <Group justify="flex-end">
                      <ConfirmButton size="compact-xs" variant="subtle" question={`Отозвать токен «${token.name}»? Программы с ним перестанут входить.`}
                        onConfirm={() => revoke.mutate(token.id)}>
                        Отозвать
                      </ConfirmButton>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Panel>
    </Stack>
  );
}
