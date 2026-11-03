import { useEffect, useState } from 'react';
import { ApiError, authorizedFetch } from './client';

export interface ImageState {
  url: string | null;
  isLoading: boolean;
  /** Почему картинки нет: текст сервера (поле error ответа) или «нет картинки». */
  error: string | null;
}

const noImage = 'нет картинки';

/** Сколько раз повторить, если очередь отрисовок на сервере полна (429). */
const busyRetries = 3;

/**
 * Картинка API для <img>: <img src> не шлёт заголовок авторизации, поэтому картинка берётся fetch с токеном и показывается
 * через URL.createObjectURL. Адрес освобождается при смене пути и размонтировании. Путь несёт версию (?v=), ответ сервер
 * разрешает кешировать навсегда — повторный показ берёт его из кеша браузера. 401 — выход на страницу входа; другая ошибка —
 * причина из ответа сервера.
 *
 * Пока <c>enabled</c> ложно (картинка не видна), запроса нет. Ушла картинка со страницы — запрос отменяется: сервер не рисует
 * то, что никто не ждёт. Очередь сервера полна (429) — повтор через Retry-After, не больше трёх раз.
 */
export function useImage(path: string | null, { enabled = true }: { enabled?: boolean } = {}): ImageState {
  const [state, setState] = useState<ImageState>({ url: null, isLoading: path !== null, error: null });

  useEffect(() => {
    if (path === null) {
      setState({ url: null, isLoading: false, error: null });
      return;
    }
    if (!enabled) return;

    const abort = new AbortController();
    let objectUrl: string | null = null;
    let retryTimer: ReturnType<typeof setTimeout> | null = null;
    setState({ url: null, isLoading: true, error: null });

    const load = (attempt: number) => {
      // 401 после неудачного обновления сессии authorizedFetch сам отправляет на вход.
      authorizedFetch(path, { signal: abort.signal })
        .then(async (response) => {
          if (response.status === 429 && attempt < busyRetries) {
            const seconds = Number(response.headers.get('Retry-After')) || 5;
            retryTimer = setTimeout(() => load(attempt + 1), seconds * 1000);
            return;
          }
          if (!response.ok) {
            const reason = await response.json().then((body: { error?: unknown }) => (typeof body?.error === 'string' && body.error ? body.error : null), () => null);
            if (!abort.signal.aborted) setState({ url: null, isLoading: false, error: reason ?? noImage });
            return;
          }
          const blob = await response.blob();
          if (abort.signal.aborted) return;
          objectUrl = URL.createObjectURL(blob);
          setState({ url: objectUrl, isLoading: false, error: null });
        })
        .catch((error: unknown) => {
          if (abort.signal.aborted || (error instanceof ApiError && error.status === 401)) return;
          setState({ url: null, isLoading: false, error: noImage });
        });
    };
    load(0);

    return () => {
      abort.abort();
      if (retryTimer) clearTimeout(retryTimer);
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [path, enabled]);

  return state;
}
