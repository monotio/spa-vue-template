#!/usr/bin/env node
/**
 * THE backend test wrapper (the only one — see the scripts budget in
 * AGENTS.md). What it adds over bare `dotnet test`:
 *
 * - Tees ANSI-stripped output to test-results/logs/test-backend-<ts>.log and
 *   prints the path first AND last — when a run fails and stdout is truncated,
 *   tail the log instead of re-running the suite.
 * - Maps signal-killed children (exit code null) to exit 1, so orchestrators
 *   that kill siblings on failure can never mistake a killed run for success.
 * - On CI, adds blame flags so a crashed/hung test produces dumps instead of
 *   a stalled runner.
 * - `--coverage` opts into instrumentation via tests.coverage.runsettings;
 *   iteration runs stay fast without it.
 *
 * Extra arguments pass through to `dotnet test`, e.g.:
 *   node scripts/run-dotnet-test.mjs --filter "ClassName=WeatherForecastControllerTests"
 */
import { spawn } from 'node:child_process';
import { createWriteStream, mkdirSync } from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const repoRoot = path.resolve(import.meta.dirname, '..');
const args = process.argv.slice(2);
const isCI = !!process.env.CI && process.env.CI !== 'false';

const coverage = args.includes('--coverage');
const passThrough = args.filter((a) => a !== '--coverage');

const dotnetArgs = ['test', '--results-directory', 'test-results/dotnet'];
if (coverage) {
  dotnetArgs.push('--collect:XPlat Code Coverage', '--settings', 'tests.coverage.runsettings');
}
if (isCI) {
  dotnetArgs.push('--blame-crash', '--blame-hang-timeout', '2m');
}
dotnetArgs.push(...passThrough);

const timestamp = new Date().toISOString().replaceAll(':', '-').replace(/\..+$/, '');
const logDir = path.join(repoRoot, 'test-results', 'logs');
mkdirSync(logDir, { recursive: true });
const logPath = path.join(logDir, `test-backend-${timestamp}.log`);
const logStream = createWriteStream(logPath);

console.log(`[run-dotnet-test] log: ${logPath}`);

// eslint-disable-next-line no-control-regex
const ansiPattern = /\u001B\[[0-9;]*m/g;

const child = spawn('dotnet', dotnetArgs, { cwd: repoRoot });
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
    console.log(`[run-dotnet-test] log: ${logPath}`);
    if (signal) {
      // Killed (e.g. by a concurrently --kill-others-on-fail sibling): code is
      // null here, and exiting 0 would mask the failure.
      console.error(`[run-dotnet-test] terminated by signal ${signal}`);
      process.exit(1);
    }
    process.exit(code ?? 1);
  });
});
