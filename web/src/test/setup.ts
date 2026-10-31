import { cleanup, configure } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => cleanup());

// Полный прогон идёт во много потоков: первая отрисовка страницы под нагрузкой бывает дольше стандартной секунды ожидания.
configure({ asyncUtilTimeout: 5000 });

// Mantine в jsdom: недостающие браузерные API.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }),
});

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(window as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub;
window.HTMLElement.prototype.scrollIntoView = () => {};
Object.defineProperty(window, 'getComputedStyle', { value: () => ({ getPropertyValue: () => '' }) });
// Textarea с autosize подписывается на загрузку шрифтов.
Object.defineProperty(document, 'fonts', { value: { addEventListener: () => {}, removeEventListener: () => {} } });
