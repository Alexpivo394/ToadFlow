import { describe, expect, it } from 'vitest';
import { daySummary, greeting, headline, noErrorsShare, sparkPath } from './greeting';

describe('greeting', () => {
  it('follows the time of day', () => {
    expect([4, 5, 11, 12, 17, 18, 22, 23].map(greeting)).toEqual([
      'Доброй ночи', 'Доброе утро', 'Доброе утро', 'Добрый день', 'Добрый день', 'Добрый вечер', 'Добрый вечер', 'Доброй ночи',
    ]);
  });

  it('headline: problems first, then todos, else calm', () => {
    expect(headline(2, 1)).toBe('2 сбоя — посмотрите ниже');
    expect(headline(1, 0)).toBe('1 сбой — посмотрите ниже');
    expect(headline(0, 3)).toBe('Всё работает, ждут вас: 3');
    expect(headline(0, 0)).toBe('Всё спокойно');
  });

  it('day summary joins module phrases', () => {
    expect(daySummary([])).toBe('За сутки работы не было.');
    expect(daySummary(['выгружено 2 модели', 'закрыта 1 коллизия'])).toBe('За сутки: выгружено 2 модели, закрыта 1 коллизия.');
  });

  it('share without errors has no NaN', () => {
    expect(noErrorsShare(0, 0)).toBe('—');
    expect(noErrorsShare(97, 3)).toBe('97 %');
  });

  it('spark path of equal values stays in the middle', () => {
    const path = sparkPath([5, 5, 5], 280, 56);
    expect(path).not.toContain('NaN');
    expect(path.startsWith('M0 28')).toBe(true);
  });
});
