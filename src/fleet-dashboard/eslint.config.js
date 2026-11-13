import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import tseslint from 'typescript-eslint'

// `npm run lint`. What this is for: the two hook rules. `tsc -b` already checks types; nothing
// checked that an effect's dependency list matched what the effect reads, and App.tsx carried
// `eslint-disable` comments for a linter that was not installed.
//
// The plugin's own "recommended" preset also turns on the React Compiler rules, which this
// codebase was not written against. They are left off on purpose, not overlooked; the two
// classic rules are named explicitly below.
export default tseslint.config(
  { ignores: ['dist/**', 'node_modules/**', 'test-results/**', 'playwright-report/**'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: { ecmaVersion: 2022, sourceType: 'module', globals: globals.browser },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      // An unused binding is an error, except one named to be ignored (`_node`) and a rest sibling
      // pulled out so the rest omits it.
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'none', ignoreRestSiblings: true,
      }],
      // `cond ? set.add(x) : set.delete(x)` as a statement is used throughout and is clear.
      '@typescript-eslint/no-unused-expressions': ['error', { allowTernary: true, allowShortCircuit: true }],
      // The input checks (journalCrossChat.ts, phoneSetup.ts) match control characters on purpose.
      'no-control-regex': 'off',
    },
  },
  {
    // Run by node (`node --test`, Playwright, Vite), not in the page.
    files: ['src/**/*.test.ts', 'tests/**/*.ts', '*.config.ts'],
    languageOptions: { globals: globals.node },
  },
)
