import type { ReactNode } from 'react';
import { Alert, Button, Center, Group, Loader, Text } from '@mantine/core';
import type { UseQueryResult } from '@tanstack/react-query';

type Query = Pick<UseQueryResult<unknown>, 'isError' | 'error' | 'refetch' | 'isFetching'>;

/** Ошибка запроса страницы: причина с сервера и «Повторить». Без ошибки — ничего. */
export function QueryError({ query, title = 'Не загрузилось' }: { query: Query; title?: string }) {
  if (!query.isError) return null;
  return (
    <Alert color="red" title={title}>
      <Group justify="space-between" wrap="nowrap" gap="sm">
        <Text size="sm">{query.error?.message || 'Сервер не ответил'}</Text>
        <Button size="xs" variant="default" loading={query.isFetching} onClick={() => void query.refetch()}>
          Повторить
        </Button>
      </Group>
    </Alert>
  );
}

/**
 * Данные запроса: пока грузится — индикатор, ошибка — причина и «Повторить», данные — <c>children(data)</c>. Пустая страница без
 * объяснения больше не показывается.
 */
export function QueryState<T>({ query, children, title }: {
  query: Query & { data: T | undefined };
  children: (data: T) => ReactNode;
  title?: string;
}) {
  if (query.isError) return <QueryError query={query} title={title} />;
  if (query.data === undefined)
    return (
      <Center py="xl">
        <Loader size="sm" />
      </Center>
    );
  return <>{children(query.data)}</>;
}
