import { useEffect, useState } from 'react';
import { ApiError, authorizedFetch } from './client';

export interface ImageState {
  url: string | null;
  isLoading: boolean;
  /** Почему картинки нет: текст сервера (поле error ответа) или «нет картинки». */
  error: string | null;
}

const noImage = 'нет картинки';

/**
 * Картинка API для <img>: <img src> не шлёт заголовок авторизации, поэтому картинка берётся fetch с токеном и показывается
 * через URL.createObjectURL. Адрес освобождается при смене пути и размонтировании. Путь несёт версию (?v=), ответ сервер
 * разрешает кешировать навсегда — повторный показ берёт его из кеша браузера. 401 — выход на страницу входа; другая ошибка —
 * причина из ответа сервера.
 */
export function useImage(path: string | null): ImageState {
  const [state, setState] = useState<ImageState>({ url: null, isLoading: path !== null, error: null });

  useEffect(() => {
    if (path === null) {
      setState({ url: null, isLoading: false, error: null });
      return;
    }

    let cancelled = false;
    let objectUrl: string | null = null;
    setState({ url: null, isLoading: true, error: null });
    // 401 после неудачного обновления сессии authorizedFetch сам отправляет на вход.
    authorizedFetch(path, {})
      .then(async (response) => {
        if (!response.ok) {
          const reason = await response.json().then((body: { error?: unknown }) => (typeof body?.error === 'string' && body.error ? body.error : null), () => null);
          if (!cancelled) setState({ url: null, isLoading: false, error: reason ?? noImage });
          return;
        }
        const blob = await response.blob();
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setState({ url: objectUrl, isLoading: false, error: null });
      })
      .catch((error: unknown) => {
        if (error instanceof ApiError && error.status === 401) return;
        if (!cancelled) setState({ url: null, isLoading: false, error: noImage });
      });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [path]);

  return state;
}
