import type { ReactNode } from 'react';
import type { UserRole } from '../api/types';
import { useAuth } from './AuthContext';

/** Показывает содержимое только пользователю с ролью не ниже указанной — так Наблюдатель не видит кнопок изменения. */
export function RequireRole({ role, children }: { role: UserRole; children: ReactNode }) {
  const { can } = useAuth();
  return can(role) ? <>{children}</> : null;
}
