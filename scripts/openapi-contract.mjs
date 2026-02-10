#!/usr/bin/env node

import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { startServer } from './server-process.mjs';

const args = new Set(process.argv.slice(2));
const checkMode = args.has('--check');
const writeMode = args.has('--write') || !checkMode;

if (checkMode && writeMode && args.has('--write')) {
  console.error('Use either --check or --write.');
  process.exit(1);
}

const outputPath = path.resolve(process.cwd(), 'docs/openapi/openapi.v1.json');
const port = Number(process.env.OPENAPI_PORT ?? 5199);
const openApiUrl = `http://127.0.0.1:${port}/openapi/v1.json`;

const server = startServer({
  port,
  environment: 'Testing',
  extraEnv: {
    OpenApi__Enabled: 'true',
    OpenTelemetry__Enabled: 'false',
  },
});

async function run() {
  await server.waitFor(openApiUrl);
  const response = await fetch(openApiUrl);
  if (!response.ok) {
    throw new Error(`Unable to fetch OpenAPI document from ${openApiUrl}.`);
  }

  const openApiDoc = await response.json();
  const normalized = `${JSON.stringify(openApiDoc, null, 2)}\n`;

  if (checkMode) {
    let current = '';
    try {
      current = await readFile(outputPath, 'utf8');
    } catch (error) {
      if (error instanceof Error && 'code' in error && error.code === 'ENOENT') {
        console.error(`OpenAPI baseline file is missing: ${outputPath}`);
        console.error('Run "npm run openapi:sync" and commit the generated file.');
        return 1;
      }

      throw error;
    }

    if (current !== normalized) {
      console.error('OpenAPI contract drift detected.');
      console.error(`Run "npm run openapi:sync" to update ${outputPath}.`);
      process.exitCode = 1;
    } else {
      console.log('OpenAPI contract is up to date.');
    }
  } else {
    await mkdir(path.dirname(outputPath), { recursive: true });
    await writeFile(outputPath, normalized, 'utf8');
    console.log(`OpenAPI contract written to ${outputPath}.`);
  }

  return process.exitCode ?? 0;
}

try {
  process.exitCode = await run();
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
