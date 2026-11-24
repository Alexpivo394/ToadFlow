/** Длительность ожидания по-русски: «42 с», «14 мин 05 с», «1 ч 10 мин». Нет значения — «—». */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '—';
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds} с`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} мин ${String(seconds % 60).padStart(2, '0')} с`;
  const hours = Math.floor(minutes / 60);
  return `${hours} ч ${minutes % 60} мин`;
}

/** Часы с одним знаком после запятой: «12,5 ч». */
export function formatHours(ms: number | null | undefined): string {
  return `${((ms ?? 0) / 3_600_000).toLocaleString('ru-RU', { maximumFractionDigits: 1, minimumFractionDigits: 0 })} ч`;
}

/** Изменение к прошлому периоду в процентах: «+12 %», «−5 %»; прошлого нет (0) — null. */
export function formatChange(now: number, before: number): string | null {
  if (!before) return null;
  const change = Math.round(((now - before) / before) * 100);
  return `${change > 0 ? '+' : change < 0 ? '−' : ''}${Math.abs(change)} %`;
}

/** Мегабайты в гигабайты для людей: «16 ГБ». */
export function formatMemory(mb: number | null | undefined): string {
  if (mb == null) return '—';
  return mb >= 1024 ? `${Math.round(mb / 1024)} ГБ` : `${mb} МБ`;
}
