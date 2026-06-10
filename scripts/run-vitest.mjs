#!/usr/bin/env node
/**
 * THE frontend test wrapper (the only one — see the scripts budget in
 * AGENTS.md). What it adds over bare `vitest run`:
 *
 * - Tees ANSI-stripped output to test-results/logs/test-frontend-<ts>.log and
 *   prints the path first AND last — tail the log instead of re-running.
 * - Forces --reporter=verbose for logged runs (the default reporter collapses
 *   to summary-only under a non-TTY pipe, which hides per-test results).
 * - Maps signal-killed children (exit code null) to exit 1.
 *
 * Multiple filter patterns OR-compose in ONE invocation (vitest treats each
 * positional as an inclusion filter):
 *   node scripts/run-vitest.mjs useFetch useAbortableRequest weather
 */
import { spawn } from 'node:child_process';
import { createWriteStream, mkdirSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const repoRoot = path.resolve(import.meta.dirname, '..');
const clientDir = path.join(repoRoot, 'vueapp1.client');
const args = process.argv.slice(2);

const timestamp = new Date().toISOString().replaceAll(':', '-').replace(/\..+$/, '');
const logDir = path.join(repoRoot, 'test-results', 'logs');
mkdirSync(logDir, { recursive: true });
const logPath = path.join(logDir, `test-frontend-${timestamp}.log`);
const logStream = createWriteStream(logPath);

console.log(`[run-vitest] log: ${logPath}`);

const ansiPattern = /\u001B\[[0-9;]*m/g;

const child = spawn(
  process.platform === 'win32' ? 'npx.cmd' : 'npx',
  ['vitest', 'run', '--reporter=verbose', ...args],
  { cwd: clientDir, shell: process.platform === 'win32' },
);
for (const [stream, out] of [
  [child.stdout, process.stdout],
  [child.stderr, process.stderr],
]) {
  stream.on('data', (chunk) => {
    out.write(chunk);
    logStream.write(String(chunk).replace(ansiPattern, ''));
  });
}

child.on('close', (code, signal) => {
  logStream.end(() => {
    console.log(`[run-vitest] log: ${logPath}`);
    if (signal) {
      console.error(`[run-vitest] terminated by signal ${signal}`);
      process.exit(1);
    }
    process.exit(code ?? 1);
  });
});
