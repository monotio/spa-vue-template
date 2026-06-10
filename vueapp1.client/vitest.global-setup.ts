import { env } from 'node:process';

export default function globalSetup(): void {
  // Deliberately NON-UTC so timezone bugs surface in tests instead of in
  // production. POSIX sign inversion applies: 'Etc/GMT-5' means UTC+5
  // (not UTC-5) — don't "correct" it.
  env['TZ'] = 'Etc/GMT-5';
}
