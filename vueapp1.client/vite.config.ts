/// <reference types="vitest/config" />

import { fileURLToPath, URL } from 'node:url';

import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-vue';
import fs from 'fs';
import path from 'path';
import child_process from 'child_process';
import { env } from 'process';

const baseFolder =
  env['APPDATA'] !== undefined && env['APPDATA'] !== ''
    ? `${env['APPDATA']}/ASP.NET/https`
    : `${env['HOME']}/.aspnet/https`;

const certificateName = 'vueapp1.client';
const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

if (!fs.existsSync(baseFolder)) {
  fs.mkdirSync(baseFolder, { recursive: true });
}

if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
  if (
    0 !==
    child_process.spawnSync(
      'dotnet',
      ['dev-certs', 'https', '--export-path', certFilePath, '--format', 'Pem', '--no-password'],
      { stdio: 'inherit' },
    ).status
  ) {
    throw new Error('Could not create certificate.');
  }
}

const target = env['ASPNETCORE_HTTPS_PORT']
  ? `https://localhost:${env['ASPNETCORE_HTTPS_PORT']}`
  : env['ASPNETCORE_URLS']
    ? env['ASPNETCORE_URLS'].split(';')[0]
    : 'https://localhost:7191';

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [plugin()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    clearMocks: true,
    restoreMocks: true,
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      include: [
        'src/composables/**/*.ts',
        'src/contracts/**/*.ts',
        'src/services/**/*.ts',
        'src/stores/**/*.ts',
        'src/utils/**/*.ts',
      ],
      exclude: ['src/**/__tests__/**'],
      thresholds: {
        lines: 85,
        functions: 85,
        statements: 85,
        branches: 80,
      },
    },
  },
  server: {
    proxy: {
      '^/api': {
        target,
        secure: false,
      },
    },
    port: parseInt(env['DEV_SERVER_PORT'] ?? '57292'),
    https: {
      key: fs.readFileSync(keyFilePath),
      cert: fs.readFileSync(certFilePath),
    },
  },
});
