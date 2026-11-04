import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

// Только правила хуков: нарушение порядка хуков — ошибка, неполные зависимости эффектов — предупреждение.
// Стиль и типы проверяют tsc и ревью; новых предупреждений не добавлять.
export default tseslint.config(
  { ignores: ['dist/**', 'node_modules/**'] },
  {
    files: ['src/**/*.{ts,tsx}'],
    languageOptions: { parser: tseslint.parser },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn',
    },
  },
);
