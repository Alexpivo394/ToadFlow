import { t } from '../i18n/ru';

/** Модули сервера, у которых есть задания: их расписания и история живут внутри модуля. */
export type ModuleName = 'export' | 'loi' | 'clash';

interface ModuleInfo {
  title: string;
  /** Вид задания, который выбирают расписания модуля. */
  kind: string;
  /** Адрес страницы расписаний модуля. */
  schedules: string;
  /** Адрес истории модуля. */
  history: string;
}

export const moduleInfo: Record<ModuleName, ModuleInfo> = {
  export: { title: t.nav.exportSpace, kind: 'export', schedules: '/export/schedules', history: '/export/history' },
  loi: { title: t.nav.loiSpace, kind: 'loi', schedules: '/loi/schedules', history: '/loi/history' },
  clash: { title: t.nav.clash, kind: 'clash-extract', schedules: '/clash/setup/schedules', history: '/clash/setup/history' },
};

/** Модуль по виду задания или расписания; неизвестный вид — выгрузка (так считали прежние версии). */
export function moduleOfKind(kind: string): ModuleName {
  return kind === 'loi' || kind === 'loi-catalog' ? 'loi' : kind === 'clash-extract' ? 'clash' : 'export';
}
