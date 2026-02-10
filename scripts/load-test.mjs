#!/usr/bin/env node

import process from 'node:process';
import { performance } from 'node:perf_hooks';
import { startServer } from './server-process.mjs';

const totalRequests = Number(process.env.LOAD_TEST_REQUESTS ?? 5000);
const concurrency = Number(process.env.LOAD_TEST_CONCURRENCY ?? 200);
const minRps = Number(process.env.LOAD_TEST_MIN_RPS ?? 0);
const port = Number(process.env.LOAD_TEST_PORT ?? 5200);
const targetUrl = `http://127.0.0.1:${port}/api/weatherforecast`;
const healthUrl = `http://127.0.0.1:${port}/health`;

const server = startServer({
  port,
  environment: 'Testing',
  extraEnv: {
    OpenTelemetry__Enabled: 'false',
  },
});

function percentile(values, p) {
  if (values.length === 0) {
    return 0;
  }

  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(sorted.length - 1, Math.floor(sorted.length * p));
  return sorted[index];
}

try {
  await server.waitFor(healthUrl);

  let issued = 0;
  let successes = 0;
  let failures = 0;
  const latenciesMs = [];

  async function worker() {
    while (true) {
      const requestNumber = issued;
      issued += 1;
      if (requestNumber >= totalRequests) {
        return;
      }

      const start = performance.now();

      try {
        const response = await fetch(targetUrl, {
          headers: { Accept: 'application/json' },
        });

        if (!response.ok) {
          failures += 1;
          continue;
        }

        await response.arrayBuffer();
        successes += 1;
      } catch {
        failures += 1;
      } finally {
        latenciesMs.push(performance.now() - start);
      }
    }
  }

  const start = performance.now();
  await Promise.all(Array.from({ length: concurrency }, () => worker()));
  const elapsedSeconds = (performance.now() - start) / 1000;
  const throughput = successes / elapsedSeconds;
  const p95 = percentile(latenciesMs, 0.95);
  const p99 = percentile(latenciesMs, 0.99);

  console.log(`Requests: ${totalRequests}`);
  console.log(`Concurrency: ${concurrency}`);
  console.log(`Successes: ${successes}`);
  console.log(`Failures: ${failures}`);
  console.log(`Elapsed: ${elapsedSeconds.toFixed(2)}s`);
  console.log(`Throughput: ${throughput.toFixed(2)} req/s`);
  console.log(`Latency p95: ${p95.toFixed(2)} ms`);
  console.log(`Latency p99: ${p99.toFixed(2)} ms`);

  if (failures > 0) {
    process.exitCode = 1;
  } else if (minRps > 0 && throughput < minRps) {
    console.error(`Throughput ${throughput.toFixed(2)} req/s is below threshold ${minRps}.`);
    process.exitCode = 1;
  }
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  const serverLogs = server.getLogs();
  if (serverLogs.length > 0) {
    console.error('\nRecent server logs:');
    console.error(serverLogs);
  }
  process.exitCode = 1;
} finally {
  await server.stop();
}
