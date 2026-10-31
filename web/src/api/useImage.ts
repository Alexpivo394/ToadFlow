import { useEffect, useState } from 'react';
import { getToken } from './client';

export interface ImageState {
  url: string | null;
  isLoading: boolean;
  missing: boolean;
}

/**
 * Картинка API для <img>: <img src> не шлёт заголовок авторизации, поэтому картинка берётся fetch с токеном и показывается
 * через URL.createObjectURL. Адрес освобождается при смене пути и размонтировании. Путь несёт версию (?v=), ответ сервер
 * разрешает кешировать навсегда — повторный показ берёт его из кеша браузера. 404 — картинки нет (missing).
 */
export function useImage(path: string | null): ImageState {
  const [state, setState] = useState<ImageState>({ url: null, isLoading: path !== null, missing: false });

  useEffect(() => {
    if (path === null) {
      setState({ url: null, isLoading: false, missing: false });
      return;
    }

    let cancelled = false;
    let objectUrl: string | null = null;
    setState({ url: null, isLoading: true, missing: false });
    const token = getToken();
    fetch(path, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
      .then(async (response) => {
        if (!response.ok) {
          if (!cancelled) setState({ url: null, isLoading: false, missing: response.status === 404 });
          return;
        }
        const blob = await response.blob();
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setState({ url: objectUrl, isLoading: false, missing: false });
      })
      .catch(() => {
        if (!cancelled) setState({ url: null, isLoading: false, missing: false });
      });

    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [path]);

  return state;
}
