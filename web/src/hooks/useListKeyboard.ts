import { useEffect, useRef } from 'react';

/** Обработчики клавиш списка: ↑ и ↓ — соседняя строка, Enter — действие над выбранной (если задано). */
export interface ListKeys {
  onUp: () => void;
  onDown: () => void;
  onEnter?: () => void;
}

/** Набор текста и диалоги не листают список: стрелки в поле двигают курсор, а не выбор под диалогом. */
function ignored(e: KeyboardEvent): boolean {
  const target = e.target as HTMLElement | null;
  if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable))
    return true;
  return document.querySelector('.mantine-Modal-content') !== null;
}

/**
 * Листание списка с клавиатуры (↑/↓, Enter) на всём окне. Не срабатывает в полях ввода и при открытом модальном окне.
 * Обработчики берутся последние, подписка одна на всё время; <c>enabled</c> — слушать ли сейчас.
 */
export function useListKeyboard(keys: ListKeys, enabled = true) {
  const current = useRef(keys);
  current.current = keys;

  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      if (ignored(e)) return;
      const { onUp, onDown, onEnter } = current.current;
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        onDown();
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        onUp();
      } else if (e.key === 'Enter' && onEnter) onEnter();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [enabled]);
}

/** Индекс строки на шаг от выбранной в пределах списка; ничего не выбрано (−1) — первая строка; пустой список — null. */
export function stepIndex(count: number, index: number, step: 1 | -1): number | null {
  if (count === 0) return null;
  if (index < 0) return 0;
  return Math.min(count - 1, Math.max(0, index + step));
}
