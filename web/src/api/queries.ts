import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { apiFetch } from './client';

/** GET-запрос с ключом: первый элемент ключа — сущность, по ней приходят живые обновления. */
export function useApi<T>(key: unknown[], path: string | null, options: { refetchInterval?: number } = {}) {
  return useQuery({
    queryKey: key,
    queryFn: () => apiFetch<T>(path!),
    enabled: path !== null,
    refetchInterval: options.refetchInterval,
  });
}

/**
 * Изменяющий запрос: при успехе — зелёное уведомление и обновление указанных списков,
 * при ошибке — красное уведомление с текстом сервера.
 */
export function useApiMutation<TBody = void, TResult = unknown>(
  method: 'POST' | 'PUT' | 'DELETE',
  path: (body: TBody) => string,
  options: { invalidate?: string[]; success?: string; json?: (body: TBody) => unknown } = {},
) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: TBody) =>
      apiFetch<TResult>(path(body), {
        method,
        json: options.json ? options.json(body) : method === 'DELETE' ? undefined : (body ?? undefined),
      }),
    onSuccess: () => {
      for (const key of options.invalidate ?? []) queryClient.invalidateQueries({ queryKey: [key] });
      if (options.success) notifications.show({ color: 'green', message: options.success });
    },
    onError: (error: Error) => notifications.show({ color: 'red', title: 'Не получилось', message: error.message }),
  });
}
