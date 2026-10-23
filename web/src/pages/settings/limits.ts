import type { VersionLimit } from '../../api/types';

export const maxInstancesPerAgent = 3;

/** Ошибка лимитов экземпляров Revit на агенте; null — всё в порядке. Те же правила, что на сервере. */
export function validateLimits(limits: VersionLimit[]): string | null {
  if (limits.some((l) => l.maxInstances < 0)) return 'Число экземпляров не может быть отрицательным';
  if (limits.reduce((sum, l) => sum + l.maxInstances, 0) > maxInstancesPerAgent) return `Не больше ${maxInstancesPerAgent} экземпляров Revit на агента`;
  return null;
}
