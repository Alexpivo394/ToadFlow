const tokenKey = 'toadflow.token';
const refreshKey = 'toadflow.refresh';

/**
 * Сессия: токен в памяти и в хранилище браузера. «Оставаться в системе» — токен и refresh в localStorage (вход переживает закрытие
 * браузера и истечение токена); без галочки — только токен в sessionStorage (вкладка закрыта — вход забыт).
 */
// Само обращение к localStorage бросает, если браузер запретил хранилище, — поэтому хранилище берётся внутри try.
const local = () => localStorage;
const session = () => sessionStorage;

let token: string | null = read(local, tokenKey) ?? read(session, tokenKey);
let refreshToken: string | null = read(local, refreshKey);

function read(storage: () => Storage, key: string): string | null {
  try {
    return storage().getItem(key);
  } catch {
    return null;
  }
}

function write(storage: () => Storage, key: string, value: string | null) {
  try {
    if (value) storage().setItem(key, value);
    else storage().removeItem(key);
  } catch {
    // Хранилище недоступно — вход продержится до перезагрузки страницы.
  }
}

export function getToken(): string | null {
  return token;
}

export function getRefreshToken(): string | null {
  return refreshToken;
}

/** Новая сессия после входа; refresh — только при «Оставаться в системе». */
export function setSession(value: string, refresh: string | null): void {
  token = value;
  refreshToken = refresh;
  const remember = refresh !== null;
  write(local, tokenKey, remember ? value : null);
  write(local, refreshKey, refresh);
  write(session, tokenKey, remember ? null : value);
}

/** Заменить токен в той же сессии (обновление, смена пароля); null — забыть сессию целиком. */
export function setToken(value: string | null): void {
  if (value === null) {
    token = null;
    refreshToken = null;
    for (const storage of [local, session]) write(storage, tokenKey, null);
    write(local, refreshKey, null);
    return;
  }
  setSession(value, refreshToken);
}

let refreshing: Promise<boolean> | null = null;

/**
 * Обменять refresh-токен на новый JWT — один запрос на все параллельные 401: сервер отзывает предъявленный токен, и второй обмен
 * того же токена закрыл бы сессию.
 */
export function refreshSession(): Promise<boolean> {
  if (!refreshToken) return Promise.resolve(false);
  refreshing ??= (async () => {
    try {
      const response = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      });
      if (!response.ok) return false;
      const body = (await response.json()) as { token: string; refreshToken: string };
      setSession(body.token, body.refreshToken);
      return true;
    } catch {
      return false;
    } finally {
      refreshing = null;
    }
  })();
  return refreshing;
}

/** Выход: сервер отзывает refresh-токен этой сессии (ответ не ждём), токены забываются. */
export function endSession(): void {
  if (refreshToken)
    void fetch('/api/auth/logout', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ refreshToken }) }).catch(
      () => {},
    );
  setToken(null);
}

const sessionPaths = ['/auth/login', '/auth/refresh', '/auth/logout'];

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    /** Тело ответа с ошибкой, если это JSON: например, конфликты сохранения черновика. */
    public readonly body: unknown = null,
  ) {
    super(message);
  }
}

let onUnauthorized: () => void = () => {};

/** Что делать, когда сервер ответил 401: обычно — выйти на страницу входа. */
export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler;
}

/** Сессия кончилась (ответ 401 вне apiFetch, например у картинки): вызвать обработчик выхода. */
export function handleUnauthorized(): void {
  onUnauthorized();
}

/**
 * Запрос с токеном. 401 — токен истёк или отозван: один раз обновить сессию (если есть refresh) и повторить; не вышло — выход.
 * Запросы входа и обновления сами по себе не обновляются.
 */
export async function authorizedFetch(path: string, init: RequestInit): Promise<Response> {
  const send = () => {
    const headers = new Headers(init.headers);
    if (token) headers.set('Authorization', `Bearer ${token}`);
    return fetch(path, { ...init, headers });
  };
  let response = await send();
  if (response.status !== 401 || sessionPaths.some((p) => path.endsWith(p))) return response;
  if (await refreshSession()) response = await send();
  if (response.status === 401) {
    onUnauthorized();
    throw new ApiError(401, 'Сессия истекла — войдите снова');
  }
  return response;
}

/** Запрос к API: JSON туда и обратно, токен в заголовке, ошибка — с текстом сервера. */
export async function apiFetch<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers = new Headers(init.headers);
  let body = init.body;
  if (init.json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(init.json);
  }

  const response = await authorizedFetch(path, { ...init, headers, body });
  if (!response.ok) throw await apiError(response);
  if (response.status === 204) return undefined as T;

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * Двоичный ответ API (сетки просмотрщика): токен в заголовке, ошибка — с текстом сервера, как у <c>apiFetch</c>. С `json` —
 * POST с этим телом (сетки сцены набора по несохранённому черновику).
 */
export async function apiFetchBinary(path: string, json?: unknown): Promise<ArrayBuffer> {
  const headers = new Headers();
  if (json !== undefined) headers.set('Content-Type', 'application/json');
  const response = await authorizedFetch(path, json === undefined ? { headers } : { method: 'POST', headers, body: JSON.stringify(json) });
  if (!response.ok) throw await apiError(response);
  return response.arrayBuffer();
}

async function apiError(response: Response): Promise<ApiError> {
  let body: { error?: unknown; title?: unknown } | null = null;
  try {
    body = await response.json();
  } catch {
    // не JSON
  }
  if (typeof body?.error === 'string') return new ApiError(response.status, body.error, body);
  if (typeof body?.title === 'string') return new ApiError(response.status, body.title, body);
  if (response.status === 403) return new ApiError(403, 'Недостаточно прав', body);
  if (response.status === 404) return new ApiError(404, 'Не найдено', body);
  return new ApiError(response.status, `Ошибка сервера (${response.status})`, body);
}

/** Скачать файл с сервера (с токеном) и отдать браузеру под именем <c>fileName</c>. */
export async function downloadFile(path: string, fileName: string): Promise<void> {
  const response = await authorizedFetch(path, {});
  if (!response.ok) throw await apiError(response);
  const url = URL.createObjectURL(await response.blob());
  const link = Object.assign(document.createElement('a'), { href: url, download: fileName });
  link.click();
  URL.revokeObjectURL(url);
}
