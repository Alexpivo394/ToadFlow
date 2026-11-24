import { describe, expect, it } from 'vitest';
import { toCsv } from './csv';

describe('toCsv', () => {
  it('semicolons, quotes where needed, decimal comma, empty for null', () => {
    expect(toCsv(['Модель', 'Часы', 'Узел'], [['АР;1.rvt', 12.5, null], ['Слово "кавычки"', 3, 'srv01']]))
      .toBe('Модель;Часы;Узел\r\n"АР;1.rvt";12,5;\r\n"Слово ""кавычки""";3;srv01');
  });
});
