import { globalIgnores } from 'eslint/config';
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript';
import pluginVue from 'eslint-plugin-vue';

export default defineConfigWithVueTs(
  {
    name: 'app/files-to-lint',
    files: ['**/*.{ts,mts,tsx,vue}'],
  },

  globalIgnores(['**/dist/**', '**/dist-ssr/**', '**/coverage/**']),

  pluginVue.configs['flat/recommended'],
  vueTsConfigs.recommendedTypeChecked,

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
    ignores: ['src/composables/useFetch.ts', 'src/**/__tests__/**'],
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
