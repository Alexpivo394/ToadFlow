import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Alert, Button, Center, Stack, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, apiFetch, endSession, getToken, setSession, setUnauthorizedHandler } from '../api/client';
import type { LoginResponse, UserRole, UserView } from '../api/types';

interface AuthState {
  user: UserView | null;
  loading: boolean;
  /** <c>remember</c> — «Оставаться в системе»: вход переживает закрытие браузера. */
  login: (login: string, password: string, remember?: boolean) => Promise<void>;
  logout: () => void;
  /** Перечитать себя после правки профиля. */
  refresh: () => Promise<void>;
  /** Роль не ниже: Наблюдатель < Координатор (Operator) < Администратор. Проектировщик — как Наблюдатель. */
  can: (role: UserRole) => boolean;
  /** Правит коллизии: проектировщик, координатор, администратор. */
  canWork: boolean;
  /** Видит ToadFlow целиком — все, кроме проектировщика. */
  isStaff: boolean;
  /** Разделы профиля; есть — есть «Мои коллизии». */
  disciplines: string[];
}

const AuthContext = createContext<AuthState | null>(null);

const rank: Record<UserRole, number> = { Viewer: 0, Designer: 0, Operator: 1, Admin: 2 };

/** Паузы повторов, пока сервер недоступен (перезапуск, обновление): дальше — последняя. */
const retryDelaysMs = [2_000, 4_000, 8_000, 15_000, 30_000];

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [user, setUser] = useState<UserView | null>(null);
  const [loading, setLoading] = useState(() => getToken() !== null);
  /** Сервер не ответил на «кто я» (не 401): токен не выбрасываем, а повторяем. */
  const [offline, setOffline] = useState(false);
  const attempt = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Данные прежнего пользователя не должны остаться в кеше: на общем компьютере их увидел бы следующий.
  const logout = useCallback(() => {
    endSession();
    queryClient.clear();
    setUser(null);
    setOffline(false);
    setLoading(false);
  }, [queryClient]);

  const loadMe = useCallback(async () => {
    if (timer.current) clearTimeout(timer.current);
    try {
      setUser(await apiFetch<UserView>('/api/auth/me'));
      setOffline(false);
      attempt.current = 0;
      setLoading(false);
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        logout();
        return;
      }
      // 502/503 или нет сети — сервер перезапускается: вход сохранён, повтор с растущей паузой.
      setOffline(true);
      const delay = retryDelaysMs[Math.min(attempt.current++, retryDelaysMs.length - 1)];
      timer.current = setTimeout(() => void loadMe(), delay);
    }
  }, [logout]);

  useEffect(() => {
    setUnauthorizedHandler(logout);
    if (getToken()) void loadMe();
    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
  }, [logout, loadMe]);

  const login = useCallback(
    async (loginName: string, password: string, remember = false) => {
      const response = await apiFetch<LoginResponse>('/api/auth/login', { method: 'POST', json: { login: loginName, password, remember } });
      queryClient.clear();
      setSession(response.token, response.refreshToken ?? null);
      setUser(response.user);
    },
    [queryClient],
  );

  const refresh = useCallback(async () => setUser(await apiFetch<UserView>('/api/auth/me')), []);

  const value = useMemo<AuthState>(
    () => ({
      user,
      loading,
      login,
      logout,
      refresh,
      can: (role) => user !== null && rank[user.role] >= rank[role],
      canWork: user !== null && user.role !== 'Viewer',
      isStaff: user !== null && user.role !== 'Designer',
      disciplines: user?.disciplines ?? [],
    }),
    [user, loading, login, logout, refresh],
  );

  if (offline && !user)
    return (
      <Center mih="100vh" p="md">
        <Stack maw={420}>
          <Alert color="yellow" title="Сервер недоступен">
            <Text size="sm">Вход сохранён. Как только сервер ответит, страница откроется сама.</Text>
          </Alert>
          <Button variant="default" onClick={() => void loadMe()}>
            Повторить
          </Button>
        </Stack>
      </Center>
    );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth вне AuthProvider');
  return context;
}
