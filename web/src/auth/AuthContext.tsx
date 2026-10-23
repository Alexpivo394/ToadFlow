import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { apiFetch, getToken, setToken, setUnauthorizedHandler } from '../api/client';
import type { LoginResponse, UserRole, UserView } from '../api/types';

interface AuthState {
  user: UserView | null;
  loading: boolean;
  login: (login: string, password: string) => Promise<void>;
  logout: () => void;
  can: (role: UserRole) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);

const rank: Record<UserRole, number> = { Viewer: 0, Operator: 1, Admin: 2 };

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserView | null>(null);
  const [loading, setLoading] = useState(() => getToken() !== null);

  const logout = useCallback(() => {
    setToken(null);
    setUser(null);
  }, []);

  useEffect(() => {
    setUnauthorizedHandler(logout);
    if (!getToken()) return;
    apiFetch<UserView>('/api/auth/me')
      .then(setUser)
      .catch(() => logout())
      .finally(() => setLoading(false));
  }, [logout]);

  const login = useCallback(async (loginName: string, password: string) => {
    const response = await apiFetch<LoginResponse>('/api/auth/login', { method: 'POST', json: { login: loginName, password } });
    setToken(response.token);
    setUser(response.user);
  }, []);

  const value = useMemo<AuthState>(
    () => ({ user, loading, login, logout, can: (role) => user !== null && rank[user.role] >= rank[role] }),
    [user, loading, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth вне AuthProvider');
  return context;
}
