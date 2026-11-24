/** Значение ячейки CSV. */
export type CsvValue = string | number | null | undefined;

/**
 * Текст CSV для Excel с русскими настройками: разделитель «;», строки в кавычках, если в них есть разделитель, кавычка или
 * перевод строки. Дробные числа — с запятой.
 */
export function toCsv(header: string[], rows: CsvValue[][]): string {
  const cell = (value: CsvValue) => {
    if (value == null) return '';
    const text = typeof value === 'number' ? String(value).replace('.', ',') : value;
    return /[;"\n\r]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
  };
  return [header, ...rows].map((row) => row.map(cell).join(';')).join('\r\n');
}

/** Скачивает таблицу файлом CSV (UTF-8 с BOM — так Excel узнаёт кодировку). */
export function downloadCsv(fileName: string, header: string[], rows: CsvValue[][]) {
  const blob = new Blob(['﻿' + toCsv(header, rows)], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName.endsWith('.csv') ? fileName : `${fileName}.csv`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
