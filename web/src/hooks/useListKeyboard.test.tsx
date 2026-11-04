import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider, Modal } from '@mantine/core';
import { describe, expect, it, vi } from 'vitest';
import { theme } from '../theme';
import { stepIndex, useListKeyboard } from './useListKeyboard';

function List({ onUp, onDown, onEnter, modal = false }: { onUp: () => void; onDown: () => void; onEnter?: () => void; modal?: boolean }) {
  useListKeyboard({ onUp, onDown, onEnter });
  return (
    <MantineProvider theme={theme}>
      <input aria-label="поиск" />
      <textarea aria-label="примечание" />
      <Modal opened={modal} onClose={() => {}} title="Подтверждение">текст</Modal>
    </MantineProvider>
  );
}

describe('useListKeyboard', () => {
  it('ListKeyboard_IgnoresInputsAndModals', async () => {
    const onUp = vi.fn();
    const onDown = vi.fn();
    const onEnter = vi.fn();
    const view = render(<List onUp={onUp} onDown={onDown} onEnter={onEnter} />);

    fireEvent.keyDown(window, { key: 'ArrowDown' });
    fireEvent.keyDown(window, { key: 'ArrowUp' });
    fireEvent.keyDown(window, { key: 'Enter' });
    expect([onDown.mock.calls.length, onUp.mock.calls.length, onEnter.mock.calls.length]).toEqual([1, 1, 1]);

    fireEvent.keyDown(view.getByLabelText('поиск'), { key: 'ArrowDown' });
    fireEvent.keyDown(view.getByLabelText('примечание'), { key: 'ArrowUp' });
    expect([onDown.mock.calls.length, onUp.mock.calls.length]).toEqual([1, 1]);

    view.rerender(<List onUp={onUp} onDown={onDown} onEnter={onEnter} modal />);
    await screen.findByRole('dialog');
    fireEvent.keyDown(window, { key: 'ArrowDown' });
    fireEvent.keyDown(document.body, { key: 'Enter' });
    expect([onDown.mock.calls.length, onEnter.mock.calls.length]).toEqual([1, 1]);
  });

  it('StepIndex_ClampsToList', () => {
    expect(stepIndex(3, -1, 1)).toBe(0);
    expect(stepIndex(3, 2, 1)).toBe(2);
    expect(stepIndex(3, 0, -1)).toBe(0);
    expect(stepIndex(0, 0, 1)).toBeNull();
  });
});
