import { pluralRu } from '../../lib/format';

/** Приветствие по часу: утро 5–11, день 12–17, вечер 18–22, иначе ночь. */
export function greeting(hour: number): string {
  if (hour >= 5 && hour <= 11) return 'Доброе утро';
  if (hour >= 12 && hour <= 17) return 'Добрый день';
  if (hour >= 18 && hour <= 22) return 'Добрый вечер';
  return 'Доброй ночи';
}

/** Как дела у системы целиком: сбои важнее дел; нет ни того ни другого — «Всё спокойно». */
export function headline(problems: number, todos: number): string {
  if (problems > 0) return `${problems} ${pluralRu(problems, ['сбой', 'сбоя', 'сбоев'])} — посмотрите ниже`;
  if (todos > 0) return `Всё работает, ждут вас: ${todos}`;
  return 'Всё спокойно';
}

/** Одна фраза о сутках из фраз модулей. */
export function daySummary(highlights: string[]): string {
  return highlights.length === 0 ? 'За сутки работы не было.' : `За сутки: ${highlights.join(', ')}.`;
}

/** Доля заданий без ошибок: «97 %»; заданий не было — «—». */
export function noErrorsShare(done: number, failed: number): string {
  const total = done + failed;
  return total === 0 ? '—' : `${Math.round((done / total) * 100)} %`;
}

/** Линия графика плитки в прямоугольнике width × height: ось от минимума до максимума, ровный ряд — посередине. */
export function sparkPath(values: number[], width: number, height: number): string {
  const min = Math.min(...values);
  const max = Math.max(...values);
  const pad = 4;
  const step = values.length > 1 ? width / (values.length - 1) : 0;
  const y = (v: number) => (max === min ? height / 2 : pad + (1 - (v - min) / (max - min)) * (height - pad * 2));
  return values.map((v, i) => `${i === 0 ? 'M' : 'L'}${Math.round(i * step * 10) / 10} ${Math.round(y(v) * 10) / 10}`).join(' ');
}
