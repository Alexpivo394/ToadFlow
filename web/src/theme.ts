import { createTheme, Input } from '@mantine/core';

export const theme = createTheme({
  primaryColor: 'green',
  defaultRadius: 'md',
  fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
  headings: { fontWeight: '650' },
  components: {
    // Подсказка под полем, а не между подписью и полем: поля в одной строке формы стоят ровно, есть у соседа подсказка или нет.
    InputWrapper: Input.Wrapper.extend({ defaultProps: { inputWrapperOrder: ['label', 'input', 'description', 'error'] } }),
  },
});
