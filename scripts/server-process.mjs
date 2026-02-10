import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

/**
 * Starts the backend with a predictable local HTTP endpoint for scripts.
 */
export function startServer({ port, environment = 'Testing', extraEnv = {} }) {
  const server = spawn('dotnet', ['run', '--project', 'VueApp1.Server', '--no-launch-profile'], {
    cwd: process.cwd(),
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: environment,
      ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
      ...extraEnv,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let logs = '';
  const appendLogs = (chunk) => {
    logs += chunk.toString();
    logs = logs.slice(-12000);
  };

  server.stdout.on('data', appendLogs);
  server.stderr.on('data', appendLogs);

  const getLogs = () => logs;

  const stop = async () => {
    if (server.exitCode !== null) {
      return;
    }

    server.kill('SIGTERM');
    await Promise.race([
      new Promise((resolve) => server.once('exit', resolve)),
      delay(3000),
    ]);

    if (server.exitCode === null) {
      server.kill('SIGKILL');
    }
  };

  const waitFor = async (url, { attempts = 120, intervalMs = 250 } = {}) => {
    for (let attempt = 1; attempt <= attempts; attempt += 1) {
      try {
        const response = await fetch(url);
        if (response.ok) {
          return;
        }
      } catch {
        // Startup race: server may still be booting.
      }

      await delay(intervalMs);
    }

    throw new Error(`Endpoint did not become ready at ${url}.`);
  };

  return { getLogs, stop, waitFor };
}
