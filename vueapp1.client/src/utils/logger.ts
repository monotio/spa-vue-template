export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

const levels: Record<LogLevel, number> = { debug: 0, info: 1, warn: 2, error: 3 };

let minLevel: LogLevel = import.meta.env.PROD ? 'warn' : 'debug';

export function setLogLevel(level: LogLevel): void {
  minLevel = level;
}

function shouldLog(level: LogLevel): boolean {
  return levels[level] >= levels[minLevel];
}

export const logger = {
  debug(...args: unknown[]): void {
    if (shouldLog('debug')) console.debug(...args);
  },
  info(...args: unknown[]): void {
    if (shouldLog('info')) console.info(...args);
  },
  warn(...args: unknown[]): void {
    if (shouldLog('warn')) console.warn(...args);
  },
  error(...args: unknown[]): void {
    if (shouldLog('error')) console.error(...args);
  },
};
