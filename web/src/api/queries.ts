import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { apiFetch } from './client';

/** GET-запрос с ключом: первый элемент ключа — сущность, по ней приходят живые обновления. */
export function useApi<T>(key: unknown[], path: string | null, options: { refetchInterval?: number } = {}) {
  return useQuery({
    queryKey: key,
    // 204 — «ничего нет»: TanStack Query не принимает undefined, поэтому null.
    queryFn: async () => ((await apiFetch<T>(path!)) ?? null) as T,
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

/**
 * Запрос-вычисление методом POST (тело — несохранённое состояние редактора): кешируется по ключу, как GET.
 * null вместо тела — запрос не нужен.
 */
export function useApiPost<T>(key: unknown[], path: string, body: unknown | null) {
  return useQuery({
    queryKey: key,
    queryFn: async () => (await apiFetch<T>(path, { method: 'POST', json: body })) as T,
    enabled: body !== null,
    placeholderData: (previous) => previous,
  });
}
