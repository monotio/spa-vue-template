import { globalIgnores } from 'eslint/config';
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript';
import pluginVue from 'eslint-plugin-vue';
import pluginVueA11y from 'eslint-plugin-vuejs-accessibility';

export default defineConfigWithVueTs(
  {
    name: 'app/files-to-lint',
    files: ['**/*.{ts,mts,tsx,vue}'],
  },

  globalIgnores(['**/dist/**', '**/dist-ssr/**', '**/coverage/**']),

  pluginVue.configs['flat/recommended'],
  vueTsConfigs.recommendedTypeChecked,
  pluginVueA11y.configs['flat/recommended'],

  {
    // Accessibility rules start as warnings: they guide without blocking.
    // Promote to 'error' once your team is ready to hold the bar in CI.
    name: 'app/a11y-warn-only',
    rules: Object.fromEntries(
      pluginVueA11y.configs['flat/recommended']
        .flatMap((config) => ('rules' in config ? Object.keys(config.rules ?? {}) : []))
        .map((rule) => [rule, 'warn']),
    ),
  },

  {
    name: 'app/strict-vue-rules',
    rules: {
      // Enforce TypeScript-first Vue SFCs
      'vue/block-lang': ['error', { script: { lang: 'ts' } }],
      'vue/define-macros-order': [
        'error',
        {
          order: ['defineProps', 'defineEmits', 'defineModel', 'defineSlots'],
          defineExposeLast: true,
        },
      ],
      'vue/define-props-declaration': ['error', 'type-based'],
      'vue/define-emits-declaration': ['error', 'type-based'],
      'vue/enforce-style-attribute': ['error', { allow: ['scoped'] }],
      'vue/no-undef-components': ['error', { ignorePatterns: ['RouterLink', 'RouterView'] }],
      'vue/no-undef-properties': 'error',
      'vue/no-unused-refs': 'error',
      'vue/no-useless-v-bind': 'error',
      'vue/prefer-true-attribute-shorthand': 'error',
      'vue/prefer-separate-static-class': 'error',
      'vue/component-api-style': ['error', ['script-setup']],

      // Prevent silent reactivity bugs
      'vue/no-ref-object-reactivity-loss': 'error',
      'vue/require-typed-ref': 'error',
      'vue/prefer-use-template-ref': 'error',
      'vue/no-required-prop-with-default': 'error',
      'vue/valid-define-options': 'error',
    },
  },
  {
    name: 'app/no-direct-fetch',
    files: ['src/**/*.{ts,mts,tsx,vue}'],
    // ReloadPrompt fetches sw.js for update checks — infrastructure, not API data.
    ignores: [
      'src/composables/useFetch.ts',
      'src/components/ReloadPrompt.vue',
      'src/**/__tests__/**',
    ],
    rules: {
      'no-restricted-globals': [
        'error',
        {
          name: 'fetch',
          message:
            'Use useFetch() in composables or dedicated API services instead of calling fetch directly.',
        },
      ],
    },
  },

  {
    name: 'app/prettier-compat',
    rules: {
      'vue/singleline-html-element-content-newline': 'off',
      'vue/max-attributes-per-line': 'off',
      'vue/html-closing-bracket-newline': 'off',
      'vue/html-indent': 'off',
      'vue/html-self-closing': 'off',
      'vue/multiline-html-element-content-newline': 'off',
    },
  },
);
