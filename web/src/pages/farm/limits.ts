import type { VersionLimit } from '../../api/types';

/** Ошибка лимитов экземпляров Revit на агенте; null — всё в порядке. Те же правила, что на сервере. */
export function validateLimits(limits: VersionLimit[]): string | null {
  if (limits.some((l) => l.maxInstances < 0)) return 'Число экземпляров не может быть отрицательным';
  return null;
}
