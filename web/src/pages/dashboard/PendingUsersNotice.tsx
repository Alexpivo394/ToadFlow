import { Link as RouterLink } from 'react-router';
import { Alert, Anchor } from '@mantine/core';
import { useApi } from '../../api/queries';
import type { UserView } from '../../api/types';
import { pluralRu } from '../../lib/format';

/** Заявки на регистрацию на общем «Обзоре»: сколько ждут и ссылка на «Пользователей». Нет заявок — ничего. */
export function PendingUsersNotice() {
  const pending = useApi<UserView[]>(['users', 'pending'], '/api/users/pending', { refetchInterval: 60_000 });
  const n = pending.data?.length ?? 0;
  if (n === 0) return null;

  return (
    <Alert color="blue" mb="md" p="xs">
      <Anchor component={RouterLink} to="/settings/users" size="sm">
        {n} {pluralRu(n, ['заявка', 'заявки', 'заявок'])} на регистрацию {pluralRu(n, ['ждёт', 'ждут', 'ждут'])} подтверждения
      </Anchor>
    </Alert>
  );
}
