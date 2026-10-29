import { describe, expect, it } from 'vitest';
import { categoryLabel } from './categories';

describe('categories', () => {
  it('categories_KnownAndUnknown', () => {
    expect(categoryLabel('OST_Walls')).toBe('Стены');
    expect(categoryLabel('OST_GenericModel')).toBe('Обобщённые модели');
    expect(categoryLabel('OST_Something')).toBe('OST_Something');
  });
});
