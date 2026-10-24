const tokenKey = 'toadflow.token';

/** Токен живёт в памяти и, если браузер позволяет, в localStorage — чтобы вход переживал перезагрузку. */
let token: string | null = readStoredToken();

function readStoredToken(): string | null {
  try {
    return localStorage.getItem(tokenKey);
  } catch {
    return null;
  }
}

export function getToken(): string | null {
  return token;
}

export function setToken(value: string | null): void {
  token = value;
  try {
    if (value) localStorage.setItem(tokenKey, value);
    else localStorage.removeItem(tokenKey);
  } catch {
    // Хранилище недоступно — вход продержится до перезагрузки страницы.
  }
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

let onUnauthorized: () => void = () => {};

/** Что делать, когда сервер ответил 401: обычно — выйти на страницу входа. */
export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler;
}

/** Запрос к API: JSON туда и обратно, токен в заголовке, ошибка — с текстом сервера. */
export async function apiFetch<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  let body = init.body;
  if (init.json !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(init.json);
  }

  const response = await fetch(path, { ...init, headers, body });
  if (response.status === 401 && !path.endsWith('/auth/login')) {
    onUnauthorized();
    throw new ApiError(401, 'Сессия истекла — войдите снова');
  }
  if (!response.ok) throw new ApiError(response.status, await errorText(response));
  if (response.status === 204) return undefined as T;

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

async function errorText(response: Response): Promise<string> {
  try {
    const body = await response.json();
    if (typeof body?.error === 'string') return body.error;
    if (typeof body?.title === 'string') return body.title;
  } catch {
    // не JSON
  }
  if (response.status === 403) return 'Недостаточно прав';
  if (response.status === 404) return 'Не найдено';
  return `Ошибка сервера (${response.status})`;
}

/** Скачать файл с сервера (с токеном) и отдать браузеру под именем <c>fileName</c>. */
export async function downloadFile(path: string, fileName: string): Promise<void> {
  const response = await fetch(path, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (!response.ok) throw new ApiError(response.status, await errorText(response));
  const url = URL.createObjectURL(await response.blob());
  const link = Object.assign(document.createElement('a'), { href: url, download: fileName });
  link.click();
  URL.revokeObjectURL(url);
}
