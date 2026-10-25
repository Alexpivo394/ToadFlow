import { afterEach, describe, expect, it, vi } from 'vitest';
import { copyText } from './clipboard';

describe('copyText', () => {
  afterEach(() => vi.restoreAllMocks());

  it('copies through a hidden field when the page is served over plain HTTP', async () => {
    let selected = '';
    document.execCommand = vi.fn(() => {
      selected = (document.activeElement as HTMLTextAreaElement).value;
      return true;
    });

    expect(await copyText('TF-AB12-CD34')).toBe(true);
    expect(selected).toBe('TF-AB12-CD34');
    expect(document.querySelector('textarea')).toBeNull();
  });

  it('reports failure when the browser refuses', async () => {
    document.execCommand = vi.fn(() => false);
    expect(await copyText('x')).toBe(false);
  });
});
