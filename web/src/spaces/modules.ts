import { type Icon, IconBooks, IconBox, IconChecklist, IconFileExport } from '@tabler/icons-react';
import { t } from '../i18n/ru';

/** Модули сервера, у которых есть задания: их расписания и история живут внутри модуля. */
export type ModuleName = 'export' | 'loi' | 'clash' | 'families';

interface ModuleInfo {
  title: string;
  /** Вид задания, который выбирают расписания модуля. */
  kind: string;
  /** Адрес страницы расписаний модуля. */
  schedules: string;
  /** Адрес истории модуля. */
  history: string;
  /** Цвет Mantine модуля на «Обзоре»: значок плитки, график, лента. */
  color: string;
  /** Значок модуля — тот же, что у его пространства. */
  icon: Icon;
}

export const moduleInfo: Record<ModuleName, ModuleInfo> = {
  export: { title: t.nav.exportSpace, kind: 'export', schedules: '/export/schedules', history: '/export/history', color: 'blue', icon: IconFileExport },
  loi: { title: t.nav.loiSpace, kind: 'loi', schedules: '/loi/schedules', history: '/loi/history', color: 'cyan', icon: IconChecklist },
  clash: { title: t.nav.clash, kind: 'clash-extract', schedules: '/clash/setup/schedules', history: '/clash/setup/history', color: 'orange', icon: IconBox },
  // Расписаний у семейств нет: задания чтения ставит сам модуль; «история» — нечитаемые файлы в «Нужно решение».
  families: { title: t.nav.families, kind: 'families-read', schedules: '/families/catalog', history: '/families/issues', color: 'violet', icon: IconBooks },
};

/** Модуль по виду задания или расписания; неизвестный вид — выгрузка (так считали прежние версии). */
export function moduleOfKind(kind: string): ModuleName {
  return kind === 'loi' || kind === 'loi-catalog' ? 'loi' : kind === 'clash-extract' ? 'clash' : kind === 'families-read' ? 'families' : 'export';
}
